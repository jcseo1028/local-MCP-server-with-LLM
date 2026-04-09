namespace LocalMcpVsExtension.Services
{
    /// <summary>
    /// 파일 확장자로 프로그래밍 언어를 감지한다.
    /// </summary>
    internal static class LanguageDetector
    {
        public static string FromFilePath(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return "text";

            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            switch (ext)
            {
                case ".cs": return "csharp";
                case ".vb": return "vb";
                case ".fs": return "fsharp";
                case ".cpp":
                case ".cxx":
                case ".cc":
                case ".h":
                case ".hpp": return "cpp";
                case ".c": return "c";
                case ".py": return "python";
                case ".js": return "javascript";
                case ".ts":
                case ".tsx": return "typescript";
                case ".java": return "java";
                case ".xml":
                case ".xaml":
                case ".csproj":
                case ".vbproj":
                case ".fsproj": return "xml";
                case ".json": return "json";
                case ".sql": return "sql";
                case ".html":
                case ".htm": return "html";
                case ".css": return "css";
                case ".ps1":
                case ".psm1": return "powershell";
                case ".sh":
                case ".bash": return "bash";
                case ".md": return "markdown";
                case ".yaml":
                case ".yml": return "yaml";
                default: return "text";
            }
        }
    }
}
