class TexCompiler {
    constructor() {
        this.currentTaskId = null;
        this.statusInterval = null;
        console.log('TexCompiler class initialized');
        this.initializeEventListeners();
    }

    initializeEventListeners() {
        const uploadForm = document.getElementById('uploadForm');
        const fileInput = document.getElementById('texFile');

        console.log('Initializing event listeners...');
        console.log('Upload form found:', !!uploadForm);
        console.log('File input found:', !!fileInput);

        if (uploadForm) {
            uploadForm.addEventListener('submit', (e) => this.handleUpload(e));
            console.log('Submit event listener attached');
        } else {
            console.error('Upload form not found! Check HTML structure');
        }

        if (fileInput) {
            fileInput.addEventListener('change', (e) => {
                console.log('File selected:', e.target.files[0]?.name);
            });
        }
    }

    async handleUpload(event) {
        event.preventDefault();
        console.log('=== UPLOAD STARTED ===');

        const fileInput = document.getElementById('texFile');
        const compileButton = document.getElementById('compileButton');

        if (!fileInput || !fileInput.files.length) {
            console.error('No file input or no files selected');
            this.showError('Пожалуйста, выберите файл');
            return;
        }

        const file = fileInput.files[0];
        console.log('Processing file:', file.name, 'Size:', file.size, 'Type:', file.type);

        // Валидация файла
        if (!file.name.toLowerCase().endsWith('.tex')) {
            this.showError('Разрешены только файлы с расширением .tex');
            return;
        }

        const formData = new FormData();
        formData.append('texFile', file);
        console.log('FormData created, entries:', formData.get('texFile')?.name || 'no file');

        try {
            // Показываем индикатор загрузки
            compileButton.disabled = true;
            compileButton.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Загрузка...';
            console.log('Button state updated');

            console.log('Sending request to /api/upload...');

            const response = await fetch('/api/upload', {
                method: 'POST',
                body: formData
            });

            console.log('Response received. Status:', response.status, 'OK:', response.ok);

            if (!response.ok) {
                const errorText = await response.text();
                console.error('HTTP error details:', errorText);
                throw new Error(`HTTP error! status: ${response.status}, details: ${errorText}`);
            }

            const result = await response.json();
            console.log('Full API Response:', JSON.stringify(result, null, 2));

            // ДЕТАЛЬНАЯ ПРОВЕРКА СТРУКТУРЫ ОТВЕТА
            if (result && result.success) {
                if (result.data && result.data.taskId) {
                    this.currentTaskId = result.data.taskId;
                    console.log('Task created with ID:', this.currentTaskId);
                    this.showStatusArea();
                    this.startStatusPolling();
                } else {
                    console.error('Task ID missing in response data:', result.data);
                    this.showError('Сервер не вернул идентификатор задачи');
                }
            } else {
                console.error('API returned unsuccessful or invalid structure:', result);
                const errorMsg = result?.error || result?.message || 'Неизвестная ошибка сервера';
                this.showError('Ошибка при загрузке файла: ' + errorMsg);
            }
        } catch (error) {
            console.error('Upload failed with error:', error);
            this.showError('Ошибка сети при загрузке файла: ' + error.message);
        } finally {
            compileButton.disabled = false;
            compileButton.innerHTML = '<i class="fas fa-compress-arrows-alt"></i> Скомпилировать';
            console.log('Button state reset');
        }
    }

    showStatusArea() {
        const statusArea = document.getElementById('statusArea');
        if (!statusArea) {
            console.error('Status area element not found!');
            return;
        }

        statusArea.style.display = 'block';
        document.getElementById('progressBar').style.width = '10%';
        document.getElementById('statusMessage').textContent = 'Файл загружен, ожидание в очереди...';
        console.log('Status area shown');
    }

    startStatusPolling() {
        if (!this.currentTaskId) {
            console.error('No task ID for polling');
            return;
        }

        console.log('Starting status polling for task:', this.currentTaskId);

        // Останавливаем предыдущий интервал если есть
        if (this.statusInterval) {
            clearInterval(this.statusInterval);
            console.log('Previous polling interval cleared');
        }

        // Опрашиваем статус каждые 5 секунды
        this.statusInterval = setInterval(() => {
            console.log('Polling status...');
            this.checkStatus();
        }, 5000);

        // Первый запрос сразу
        this.checkStatus();
    }

    async checkStatus() {
        if (!this.currentTaskId) {
            console.error('No current task ID for status check');
            return;
        }

        const url = `/api/status/${this.currentTaskId}`;
        console.log('Checking status at:', url);

        try {
            const response = await fetch(url);
            console.log('Status response - Status:', response.status, 'OK:', response.ok);

            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            const result = await response.json();
            console.log('Status response JSON:', result);

            if (result.success) {
                this.updateUI(result.data);

                // Если задача завершена (успешно или с ошибкой), останавливаем опрос
                if (result.data.status === 'Completed' || result.data.status === 'Failed') {
                    console.log('Task completed, stopping polling. Status:', result.data.status);
                    clearInterval(this.statusInterval);
                    this.statusInterval = null;
                }
            } else {
                console.error('Status API returned error:', result.error);
                this.showError(result.error || 'Ошибка при проверке статуса');
                clearInterval(this.statusInterval);
                this.statusInterval = null;
            }
        } catch (error) {
            console.error('Status check failed:', error);
            this.showError('Ошибка сети при проверке статуса: ' + error.message);
            clearInterval(this.statusInterval);
            this.statusInterval = null;
        }
    }

    updateUI(status) {
        console.log('Updating UI with status:', status);

        const progressBar = document.getElementById('progressBar');
        const statusMessage = document.getElementById('statusMessage');
        const errorMessage = document.getElementById('errorMessage');
        const successArea = document.getElementById('successArea');
        const downloadLink = document.getElementById('downloadLink');
        const fileInfo = document.getElementById('fileInfo');

        console.log('FileInfo element:', fileInfo); // Добавим отладку

        if (!progressBar || !statusMessage) {
            console.error('Required UI elements not found');
            return;
        }

        // Скрываем ошибку по умолчанию
        if (errorMessage) errorMessage.style.display = 'none';

        // Функция для отображения длительности
        const displayDuration = (prefix) => {
            if (fileInfo && status.duration !== undefined && status.duration !== null) {
                console.log('Duration value:', status.duration, 'Type:', typeof status.duration);
                try {
                    const formatted = this.formatDuration(status.duration);
                    fileInfo.textContent = `${prefix}: ${formatted}`;
                } catch (error) {
                    console.error('Error formatting duration:', error);
                    fileInfo.textContent = `${prefix}: ${status.duration} мс`;
                }
            } else {
                console.log('No duration available');
                fileInfo.textContent = '';
            }
        };

        switch (status.status) {
            case 'Queued':
                progressBar.style.width = '20%';
                progressBar.className = 'progress-bar progress-bar-striped progress-bar-animated bg-info';
                statusMessage.textContent = 'В очереди на обработку...';
                displayDuration('В очереди');
                break;

            case 'Processing':
                progressBar.style.width = '50%';
                progressBar.className = 'progress-bar progress-bar-striped progress-bar-animated bg-warning';
                statusMessage.textContent = 'Идет компиляция...';
                displayDuration('Обрабатывается');
                break;

            case 'Completed':
                progressBar.style.width = '100%';
                progressBar.className = 'progress-bar bg-success';
                statusMessage.textContent = 'Компиляция успешно завершена!';

                if (successArea) {
                    successArea.style.display = 'block';
                    console.log('Success area shown');
                }

                if (downloadLink && status.downloadUrl) {
                    downloadLink.href = status.downloadUrl;
                    downloadLink.style.display = 'inline-block';
                    console.log('Download link set to:', status.downloadUrl);
                }

                displayDuration('Время компиляции');

                // ОСТАНАВЛИВАЕМ POLLING при завершении
                if (this.statusInterval) {
                    console.log('Stopping polling for completed task');
                    clearInterval(this.statusInterval);
                    this.statusInterval = null;
                }
                break;

            case 'Failed':
                progressBar.style.width = '100%';
                progressBar.className = 'progress-bar bg-danger';
                statusMessage.textContent = 'Ошибка компиляции';

                if (errorMessage) {
                    errorMessage.textContent = status.errorMessage || 'Неизвестная ошибка';
                    errorMessage.style.display = 'block';
                }

                displayDuration('Время до ошибки');

                // ОСТАНАВЛИВАЕМ POLLING при ошибке
                if (this.statusInterval) {
                    console.log('Stopping polling for failed task');
                    clearInterval(this.statusInterval);
                    this.statusInterval = null;
                }
                break;

            default:
                console.warn('Unknown status:', status.status);
        }

        console.log('UI updated successfully');
    }

    showError(message) {
        console.error('Showing error:', message);

        const errorMessage = document.getElementById('errorMessage');
        const statusArea = document.getElementById('statusArea');

        if (errorMessage) {
            errorMessage.textContent = message;
            errorMessage.style.display = 'block';
        }

        if (statusArea) {
            statusArea.style.display = 'block';
        }
    }

    formatDuration(milliseconds) {
        // Преобразуем в число на случай, если пришла строка
        const ms = Number(milliseconds);

        if (isNaN(ms)) {
            console.error('Invalid duration value:', milliseconds);
            return 'неизвестно';
        }

        const seconds = Math.round(ms / 1000);
        if (seconds < 60) {
            return `${seconds} сек.`;
        } else {
            const minutes = Math.floor(seconds / 60);
            const remainingSeconds = seconds % 60;
            if (minutes < 60) {
                return `${minutes} мин. ${remainingSeconds} сек.`;
            } else {
                const hours = Math.floor(minutes / 60);
                const remainingMinutes = minutes % 60;
                return `${hours} ч. ${remainingMinutes} мин.`;
            }
        }
    }
}

// Инициализация при загрузке страницы
console.log('Document loading...');
document.addEventListener('DOMContentLoaded', () => {
    console.log('DOM fully loaded, initializing TexCompiler...');
    try {
        new TexCompiler();
        console.log('TexCompiler initialized successfully');
    } catch (error) {
        console.error('Failed to initialize TexCompiler:', error);
    }
});

// Также инициализируем при полной загрузке страницы (на всякий случай)
window.addEventListener('load', () => {
    console.log('Window fully loaded');
});