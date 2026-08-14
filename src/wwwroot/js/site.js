/* ============================================================================
   TexCompiler — интерфейс страницы компиляции.

   Контракт с сервером не менялся: POST /api/upload отдаёт { success, data.taskId },
   GET /api/status/{id} — { success, data: { status, duration, queuePosition,
   downloadUrl, errorMessage } }, а PDF и лог забираются с /api/download/{id}
   и /api/download-log/{id}.

   Видимость областей переключается атрибутом hidden, а не инлайновым style.display:
   состояние элемента тогда видно в разметке, и CSS не приходится перебивать
   инлайновыми правилами.
   ========================================================================= */

const THEME_STORAGE_KEY = 'tex-compiler-theme';
const POLL_INTERVAL_MS = 5000;
const ALLOWED_EXTENSIONS = ['.tex', '.zip'];

/**
 * Ширина полосы прогресса по состоянию задачи. Реального процента у компиляции нет:
 * pdflatex не сообщает о продвижении, поэтому значения обозначают этап, а не долю
 * сделанной работы. Движение внутри этапа отдано бегущим полосам в CSS.
 */
const PROGRESS_BY_STATE = {
    uploading: 12,
    Queued: 30,
    Processing: 65,
    Completed: 100,
    Failed: 100
};

/** Состояния трёх шагов: очередь, компиляция, готово. */
const STEPS_BY_STATE = {
    uploading: ['active', 'pending', 'pending'],
    Queued: ['active', 'pending', 'pending'],
    Processing: ['done', 'active', 'pending'],
    Completed: ['done', 'done', 'done'],
    Failed: ['done', 'failed', 'pending']
};

function setVisible (element, visible) {
    if (element) {
        element.hidden = !visible;
    }
}

function formatFileSize (bytes) {
    if (bytes >= 1024 * 1024) {
        return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`;
    }

    return `${Math.max(1, Math.round(bytes / 1024))} КБ`;
}

class TexCompiler {
    constructor () {
        this.currentTaskId = null;
        this.statusInterval = null;

        // Источник истины о выбранном файле — это поле, а не input.files: файл может
        // прийти перетаскиванием, а записать его в input.files умеют не все браузеры.
        this.selectedFile = null;

        this.elements = {
            form: document.getElementById('uploadForm'),
            fileInput: document.getElementById('texFile'),
            dropZone: document.getElementById('dropZone'),
            dropZoneIdle: document.getElementById('dropZoneIdle'),
            dropZoneFile: document.getElementById('dropZoneFile'),
            selectedFileName: document.getElementById('selectedFileName'),
            selectedFileSize: document.getElementById('selectedFileSize'),
            clearFileButton: document.getElementById('clearFileButton'),
            compileButton: document.getElementById('compileButton'),
            compileButtonIcon: document.getElementById('compileButtonIcon'),
            compileButtonLabel: document.getElementById('compileButtonLabel'),
            formError: document.getElementById('formError'),

            statusArea: document.getElementById('statusArea'),
            taskIdLabel: document.getElementById('taskIdLabel'),
            steps: document.getElementById('steps'),
            progressBar: document.getElementById('progressBar'),
            statusMessage: document.getElementById('statusMessage'),
            statusText: document.getElementById('statusText'),
            fileInfo: document.getElementById('fileInfo'),
            queueInfo: document.getElementById('queueInfo'),

            // errorMessage — плашка в панели статуса: сеть, пропавшая задача. Причина
            // неудачной компиляции идёт в compileErrorText внутри errorArea. Раньше
            // оба элемента назывались errorMessage, и getElementById возвращал первый,
            // из-за чего второй всегда оставался пустым.
            errorMessage: document.getElementById('errorMessage'),
            successArea: document.getElementById('successArea'),
            errorArea: document.getElementById('errorArea'),
            compileErrorText: document.getElementById('compileErrorText'),
            downloadLink: document.getElementById('downloadLink'),
            downloadLogLink: document.getElementById('downloadLogLink'),
            resetButton: document.getElementById('resetButton')
        };

        this.maxFileSizeMegabytes = Number(this.elements.dropZone?.dataset.maxSizeMb) || 20;

        this.bindEvents();
    }

    bindEvents () {
        const { form, fileInput, dropZone, clearFileButton, resetButton, downloadLogLink } = this.elements;

        if (!form || !fileInput || !dropZone) {
            console.error('TexCompiler: разметка страницы компиляции не найдена');
            return;
        }

        form.addEventListener('submit', (event) => this.handleUpload(event));
        fileInput.addEventListener('change', () => this.selectFile(fileInput.files[0] || null));

        clearFileButton?.addEventListener('click', () => this.selectFile(null));
        resetButton?.addEventListener('click', () => this.reset());
        downloadLogLink?.addEventListener('click', () => this.downloadLog(this.currentTaskId));

        this.bindDragAndDrop(dropZone);
    }

    bindDragAndDrop (dropZone) {
        // Без preventDefault на dragover браузер не считает элемент приёмником и drop
        // до него не доходит.
        dropZone.addEventListener('dragover', (event) => {
            event.preventDefault();
            dropZone.classList.add('is-dragover');
        });

        // dragleave приходит и при переходе курсора на вложенный элемент: подсветку
        // снимаем только когда курсор действительно вышел за пределы зоны.
        dropZone.addEventListener('dragleave', (event) => {
            if (!dropZone.contains(event.relatedTarget)) {
                dropZone.classList.remove('is-dragover');
            }
        });

        dropZone.addEventListener('drop', (event) => {
            event.preventDefault();
            dropZone.classList.remove('is-dragover');
            this.selectFile(event.dataTransfer?.files[0] || null);
        });

        // Файл, отпущенный мимо зоны, браузер по умолчанию открывает вместо страницы,
        // и незакончённая работа теряется.
        window.addEventListener('dragover', (event) => event.preventDefault());
        window.addEventListener('drop', (event) => event.preventDefault());
    }

    selectFile (file) {
        const { fileInput, dropZone, dropZoneIdle, dropZoneFile, selectedFileName, selectedFileSize } = this.elements;

        this.selectedFile = file;
        this.hideFormError();

        if (!file) {
            // Сброс value обязателен: иначе повторный выбор того же файла не вызовет
            // событие change, и зона останется пустой.
            fileInput.value = '';
            dropZone.classList.remove('has-file');
            setVisible(dropZoneIdle, true);
            setVisible(dropZoneFile, false);
            return;
        }

        selectedFileName.textContent = file.name;
        selectedFileSize.textContent = formatFileSize(file.size);
        dropZone.classList.add('has-file');
        setVisible(dropZoneIdle, false);
        setVisible(dropZoneFile, true);
    }

    /** @returns {string|null} причина отказа или null, если файл подходит */
    validateFile (file) {
        const name = file.name.toLowerCase();

        if (!ALLOWED_EXTENSIONS.some((extension) => name.endsWith(extension))) {
            return `Разрешены только файлы ${ALLOWED_EXTENSIONS.join(' и ')}`;
        }

        // Проверка до отправки: сервер тот же файл отвергнет, но только после того,
        // как примет его целиком.
        if (file.size > this.maxFileSizeMegabytes * 1024 * 1024) {
            return `Файл занимает ${formatFileSize(file.size)}, а сервис принимает до ${this.maxFileSizeMegabytes} МБ`;
        }

        return null;
    }

    async handleUpload (event) {
        event.preventDefault();

        if (!this.selectedFile) {
            this.showFormError('Выберите файл для компиляции');
            return;
        }

        const validationError = this.validateFile(this.selectedFile);
        if (validationError) {
            this.showFormError(validationError);
            return;
        }

        const formData = new FormData();
        formData.append('texFile', this.selectedFile);

        this.hideFormError();
        this.setBusy(true);
        this.showStatusArea();

        try {
            const response = await fetch('/api/upload', {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                // Сервер обрывает приём тела на превышении лимита и отвечает голым 413,
                // без тела с объяснением: сообщение приходится собирать здесь.
                if (response.status === 413) {
                    this.failBeforeTask('Файл слишком большой: сервер прервал загрузку, не приняв его целиком');
                    return;
                }

                // Отказ валидации приходит с кодом 400 и телом той же формы
                // { success, error }, что и успешный ответ, — там лежит объяснение
                // на русском, и показать нужно именно его, а не код ответа.
                const serverMessage = await this.readErrorMessage(response);

                console.error('Upload rejected:', response.status, serverMessage);
                this.failBeforeTask(serverMessage || `Сервер отклонил загрузку (HTTP ${response.status})`);
                return;
            }

            const result = await response.json();

            if (!result?.success) {
                this.failBeforeTask(result?.error || result?.message || 'Сервер отклонил загрузку без объяснения причины');
                return;
            }

            if (!result.data?.taskId) {
                console.error('Task ID missing in response data:', result.data);
                this.failBeforeTask('Сервер не вернул идентификатор задачи');
                return;
            }

            this.currentTaskId = result.data.taskId;
            this.elements.taskIdLabel.textContent = this.currentTaskId;
            this.startStatusPolling();
        } catch (error) {
            console.error('Upload failed with error:', error);
            this.failBeforeTask(`Не удалось отправить файл: ${error.message}`);
        } finally {
            this.setBusy(false);
        }
    }

    /** @returns {Promise<string|null>} объяснение отказа из тела ответа, если оно там есть */
    async readErrorMessage (response) {
        try {
            const body = await response.json();

            return body?.error || body?.message || null;
        } catch (error) {
            // Тела нет или это не JSON — например, страница ошибки от прокси.
            return null;
        }
    }

    /**
     * Отказ до того, как задача появилась: панель статуса ещё ничего не показывает,
     * поэтому она закрывается, а причина остаётся у зоны выбора файла.
     */
    failBeforeTask (message) {
        setVisible(this.elements.statusArea, false);
        this.showFormError(message);
    }

    setBusy (busy) {
        const { compileButton, compileButtonIcon, compileButtonLabel } = this.elements;

        compileButton.disabled = busy;
        compileButtonIcon?.classList.toggle('spin', busy);
        compileButtonLabel.textContent = busy ? 'Отправка...' : 'Скомпилировать';
    }

    showStatusArea () {
        const { statusArea, taskIdLabel } = this.elements;

        taskIdLabel.textContent = '';
        this.hideResultAreas();
        this.render({ status: 'uploading' });
        setVisible(statusArea, true);
    }

    hideResultAreas () {
        const { successArea, errorArea, errorMessage, compileErrorText, queueInfo } = this.elements;

        setVisible(successArea, false);
        setVisible(errorArea, false);
        setVisible(errorMessage, false);
        setVisible(queueInfo, false);

        // Причина прошлого отказа не должна всплыть при следующей загрузке.
        compileErrorText.textContent = '';
    }

    startStatusPolling () {
        if (!this.currentTaskId) {
            console.error('No task ID for polling');
            return;
        }

        this.stopStatusPolling();
        this.statusInterval = setInterval(() => this.checkStatus(), POLL_INTERVAL_MS);

        // Первый запрос сразу: иначе первые секунды страница показывает «загружен»,
        // хотя задача уже может быть в работе.
        this.checkStatus();
    }

    stopStatusPolling () {
        if (this.statusInterval) {
            clearInterval(this.statusInterval);
            this.statusInterval = null;
        }
    }

    async checkStatus () {
        if (!this.currentTaskId) {
            return;
        }

        try {
            const response = await fetch(`/api/status/${this.currentTaskId}`);

            if (!response.ok) {
                // 404 значит, что задачи больше нет в памяти сервиса: состояние живёт
                // только в процессе, поэтому перезапуск — в том числе штатный, при каждом
                // деплое — стирает её. Раньше это попадало в общий catch и показывалось
                // как ошибка сети, хотя сеть в порядке, и пользователь не понимал,
                // что делать.
                if (response.status === 404) {
                    console.warn('Task not found — the service was probably restarted');
                    this.showError('Задача не найдена — вероятно, сервис перезапускался. Отправьте файл заново.');
                    this.stopStatusPolling();
                    return;
                }

                throw new Error(`HTTP error! status: ${response.status}`);
            }

            const result = await response.json();

            if (!result.success) {
                console.error('Status API returned error:', result.error);
                this.showError(result.error || 'Ошибка при проверке статуса');
                this.stopStatusPolling();
                return;
            }

            this.render(result.data);

            if (result.data.status === 'Completed' || result.data.status === 'Failed') {
                this.stopStatusPolling();
            }
        } catch (error) {
            console.error('Status check failed:', error);
            this.showError(`Ошибка сети при проверке статуса: ${error.message}`);
            this.stopStatusPolling();
        }
    }

    render (status) {
        const state = status.status;
        const {
            progressBar, statusMessage, statusText, fileInfo, queueInfo,
            successArea, errorArea, errorMessage, compileErrorText, downloadLink
        } = this.elements;

        if (!(state in PROGRESS_BY_STATE)) {
            console.warn('Unknown status:', state);
            return;
        }

        const percent = PROGRESS_BY_STATE[state];
        progressBar.style.width = `${percent}%`;
        progressBar.dataset.state = state.toLowerCase();
        progressBar.setAttribute('aria-valuenow', String(percent));

        this.renderSteps(state);

        statusMessage.dataset.state = state.toLowerCase();
        statusText.textContent = {
            uploading: 'Файл отправляется...',
            Queued: 'В очереди на обработку',
            Processing: 'Идёт компиляция',
            Completed: 'Компиляция успешно завершена',
            Failed: 'Компиляция не удалась'
        }[state];

        // Ошибки предыдущего опроса не переносим: показываем то, что говорит сервер сейчас.
        setVisible(errorMessage, false);
        setVisible(successArea, false);
        setVisible(errorArea, false);
        setVisible(queueInfo, false);

        fileInfo.textContent = this.formatElapsed(state, status.duration);

        // Позиция осмысленна только пока задача стоит в очереди. Условие >= 1
        // перенесено из прежнего кода как есть — оно пропускает и позицию 1, когда
        // впереди никого нет. Это отдельный дефект (P24): правится он вместе
        // с формулировкой сообщения, поэтому здесь поведение не менялось.
        if (state === 'Queued' && status.queuePosition >= 1) {
            queueInfo.textContent = `Позиция в очереди: ${status.queuePosition}`;
            setVisible(queueInfo, true);
        }

        if (state === 'Completed') {
            if (status.downloadUrl) {
                downloadLink.href = status.downloadUrl;
            }
            setVisible(successArea, true);
        }

        if (state === 'Failed') {
            compileErrorText.textContent = status.errorMessage || 'Неизвестная ошибка';
            setVisible(errorArea, true);
        }
    }

    renderSteps (state) {
        const states = STEPS_BY_STATE[state];

        Array.from(this.elements.steps.children).forEach((step, index) => {
            step.dataset.state = states[index];
        });
    }

    formatElapsed (state, duration) {
        if (duration === undefined || duration === null) {
            return '';
        }

        const prefix = {
            Queued: 'В очереди',
            Processing: 'Обрабатывается',
            Completed: 'Время компиляции',
            Failed: 'Время до ошибки'
        }[state];

        return prefix ? `${prefix}: ${this.formatDuration(duration)}` : '';
    }

    async downloadLog (taskId) {
        if (!taskId) {
            this.showError('Не удалось найти задачу для скачивания лога');
            return;
        }

        try {
            const response = await fetch(`/api/download-log/${taskId}`);

            if (!response.ok) {
                throw new Error(response.status === 404
                    ? 'Лог компиляции не найден'
                    : `HTTP error! status: ${response.status}`);
            }

            // Имя файла задаёт сервер: у него есть исходное имя загруженного файла.
            let fileName = `compile_log_${taskId}.txt`;
            const contentDisposition = response.headers.get('Content-Disposition');

            if (contentDisposition) {
                const match = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                if (match && match[1]) {
                    fileName = match[1].replace(/['"]/g, '');
                }
            }

            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const link = document.createElement('a');

            link.hidden = true;
            link.href = url;
            link.download = fileName;

            document.body.appendChild(link);
            link.click();

            window.URL.revokeObjectURL(url);
            document.body.removeChild(link);
        } catch (error) {
            console.error('Log download failed:', error);
            this.showError(`Ошибка при скачивании лога: ${error.message}`);
        }
    }

    /** Отказ по уже существующей задаче: показывается в панели статуса. */
    showError (message) {
        const { errorMessage, statusArea } = this.elements;

        errorMessage.textContent = message;
        setVisible(errorMessage, true);
        setVisible(statusArea, true);
    }

    showFormError (message) {
        const { formError } = this.elements;

        formError.textContent = message;
        setVisible(formError, true);
    }

    hideFormError () {
        setVisible(this.elements.formError, false);
    }

    /** Возврат к пустой форме после успешной сборки. */
    reset () {
        this.stopStatusPolling();
        this.currentTaskId = null;
        this.selectFile(null);
        this.hideResultAreas();
        setVisible(this.elements.statusArea, false);
        this.elements.fileInput.focus();
    }

    formatDuration (milliseconds) {
        const ms = Number(milliseconds);

        if (isNaN(ms)) {
            console.error('Invalid duration value:', milliseconds);
            return 'неизвестно';
        }

        const seconds = Math.round(ms / 1000);
        if (seconds < 60) {
            return `${seconds} сек.`;
        }

        const minutes = Math.floor(seconds / 60);
        if (minutes < 60) {
            return `${minutes} мин. ${seconds % 60} сек.`;
        }

        return `${Math.floor(minutes / 60)} ч. ${minutes % 60} мин.`;
    }
}

/**
 * Переключатель темы. Начальное значение выставляет инлайновый скрипт в head —
 * здесь остаётся только реакция на нажатие и запоминание выбора.
 */
function initThemeToggle () {
    const toggle = document.getElementById('themeToggle');

    toggle?.addEventListener('click', () => {
        const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';

        document.documentElement.dataset.theme = next;

        try {
            localStorage.setItem(THEME_STORAGE_KEY, next);
        } catch (error) {
            // Приватный режим: тема продержится до перезагрузки страницы.
        }
    });
}

document.addEventListener('DOMContentLoaded', () => {
    // Страховка от повторной инициализации. Скрипт уже был однажды подключён на
    // странице дважды, и тогда создавалось два экземпляра TexCompiler: каждый вешал
    // свой обработчик submit, и один клик отправлял на сервер две задачи.
    if (window.__texCompilerInitialized) {
        console.warn('TexCompiler already initialized, skipping duplicate initialization');
        return;
    }
    window.__texCompilerInitialized = true;

    initThemeToggle();

    // Скрипт подключён в _Layout, то есть работает и на странице ошибки, где формы
    // компиляции нет: там инициализировать нечего.
    if (document.getElementById('uploadForm')) {
        try {
            new TexCompiler();
        } catch (error) {
            console.error('Failed to initialize TexCompiler:', error);
        }
    }
});
