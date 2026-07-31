namespace TexCompiler.Models
{
	public class UploadRequest
	{
		[AllowedFileExtensions(".tex", ".zip")]
		public IFormFile TexFile { get; set; }
	}
}
