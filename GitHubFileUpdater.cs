using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarframeTracker;

/// <summary>
/// Проверяет содержимое ветки main репозитория q7vych/w-tracker.
/// Синхронизирует только файлы интерфейса и статические ресурсы;
/// пользовательские данные и исходники/бинарники приложения не затрагиваются.
/// </summary>
internal sealed class GitHubFileUpdater
{
    private const string Owner = "q7vych";
    private const string Repository = "w-tracker";
    private const string Branch = "main";
    private const string BranchUrl = $"https://api.github.com/repos/{Owner}/{Repository}/branches/{Branch}";
    private const string TreeUrlTemplate = $"https://api.github.com/repos/{Owner}/{Repository}/git/trees/{{0}}?recursive=1";
    private const string RawBaseUrl = $"https://raw.githubusercontent.com/{Owner}/{Repository}/{Branch}/";

    private static readonly HttpClient Http = CreateClient();
    private readonly string installDirectory;
    private readonly string backupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarframeTracker", "UpdateBackups");

    public GitHubFileUpdater(string installDirectory) => this.installDirectory = installDirectory;

    public async Task<IReadOnlyList<string>> CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        // Сначала получаем реальный SHA дерева ветки. Git Trees API надёжнее
        // вызывать по SHA дерева, а не передавать имя ветки напрямую.
        using var branchResponse = await Http.GetAsync(BranchUrl, cancellationToken);
        var branchJson = await ReadSuccessOrThrowAsync(branchResponse, "получение ветки main", cancellationToken);
        using var branchDoc = JsonDocument.Parse(branchJson);
        var treeSha = branchDoc.RootElement
            .GetProperty("commit")
            .GetProperty("commit")
            .GetProperty("tree")
            .GetProperty("sha")
            .GetString();
        if (string.IsNullOrWhiteSpace(treeSha))
            throw new InvalidDataException("GitHub не вернул SHA дерева ветки main.");

        using var response = await Http.GetAsync(string.Format(TreeUrlTemplate, Uri.EscapeDataString(treeSha)), cancellationToken);
        var treeJson = await ReadSuccessOrThrowAsync(response, "получение списка файлов", cancellationToken);
        using var treeDoc = JsonDocument.Parse(treeJson);
        var root = treeDoc.RootElement;
        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub не вернул дерево файлов репозитория.");
        if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("GitHub вернул неполный список файлов репозитория.");

        var updated = new List<string>();
        foreach (var entry in tree.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.TryGetProperty("type", out var type) || type.GetString() != "blob") continue;
            if (!entry.TryGetProperty("path", out var pathElement)) continue;
            var relativePath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(relativePath) || !IsAllowedPath(relativePath)) continue;
            if (!entry.TryGetProperty("sha", out var shaElement)) continue;
            var remoteSha = shaElement.GetString();
            if (string.IsNullOrWhiteSpace(remoteSha)) continue;

            var localPath = GetSafeLocalPath(relativePath);
            if (File.Exists(localPath))
            {
                var localSha = await CalculateGitBlobShaAsync(localPath, cancellationToken);
                if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase)) continue;
            }

            var downloadUrl = RawBaseUrl + string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));
            using var fileResponse = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            fileResponse.EnsureSuccessStatusCode();
            var bytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var downloadedSha = CalculateGitBlobSha(bytes);
            if (!string.Equals(downloadedSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-проверка не пройдена: {relativePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            if (File.Exists(localPath))
            {
                var backupPath = Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(localPath, backupPath, overwrite: true);
            }

            var tempPath = localPath + ".update.tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            if (File.Exists(localPath)) File.Move(tempPath, localPath, overwrite: true);
            else File.Move(tempPath, localPath);
            updated.Add(relativePath);
        }
        return updated;
    }

    private static async Task<string> ReadSuccessOrThrowAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return body;

        string? detail = null;
        try
        {
            using var errorDoc = JsonDocument.Parse(body);
            if (errorDoc.RootElement.TryGetProperty("message", out var message))
                detail = message.GetString();
        }
        catch { /* Ответ мог быть не JSON. */ }

        var status = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $". GitHub: {detail}";
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            suffix += " Проверь, что репозиторий публичный и ветка main существует.";
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            suffix += " Возможно, исчерпан лимит запросов GitHub API. Попробуй позже.";

        throw new HttpRequestException($"Ошибка при {operation}: {status}{suffix}", null, response.StatusCode);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WarframeTracker-Updater/2.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static bool IsAllowedPath(string path)
    {
        if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..")) return false;
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(normalized);
        // Эти данные принадлежат пользователю или обновляются отдельными функциями.
        if (name is "tasks.json" or "settings.json" or "builds.json" or "mods.json" or "update-manifest.json") return false;
        if (normalized.Split('/').Any(p => p.StartsWith('.') || p is "userdata" or "user-data" or "backups" or "publish" or "bin" or "obj")) return false;

        var extension = Path.GetExtension(normalized);
        return extension is ".html" or ".css" or ".js" or ".json" or ".svg" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".woff" or ".woff2" or ".ttf";
    }

    private string GetSafeLocalPath(string relativePath)
    {
        var root = Path.GetFullPath(installDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(installDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Недопустимый путь обновления: " + relativePath);
        return fullPath;
    }

    private static async Task<string> CalculateGitBlobShaAsync(string path, CancellationToken cancellationToken)
        => CalculateGitBlobSha(await File.ReadAllBytesAsync(path, cancellationToken));

    private static string CalculateGitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var input = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, input, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, input, header.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
    }
}
