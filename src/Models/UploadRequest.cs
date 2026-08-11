namespace TexCompiler.Models
{
	public class UploadRequest
	{
		[AllowFileExtensions(".tex", ".zip")]
		public IFormFile TexFile { get; set; }
	}
}
