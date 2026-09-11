using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.ServiceModel.Syndication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace PodcastAutosFeeds;

public record FeedSource(string Name, string Url);

public record Article(string Title, string Link, string Source, DateTimeOffset? PublishedAt, string Summary, string Image = "");

public class Program
{
    private const string DbFileName = "noticias.db";
    private const string FeedsConfigName = "feeds.json";
    private const int MaxAttempts = 3;
    private const int MaxFeedPages = 5;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _rootDir = Directory.GetCurrentDirectory();

    public static async Task Main(string[] args)
    {
        _rootDir = FindRoot();
        var feeds = LoadFeeds();
        InitDb();

        var nuevos = new List<Article>();

        using var http = CreateHttpClient();

        foreach (var feed in feeds)
        {
            try
            {
                var articles = await FetchFeedWithRetryAsync(http, feed);
                var validos = 0;
                foreach (var article in articles)
                {
                    if (string.IsNullOrWhiteSpace(article.Link))
                        continue;
                    validos++;
                    var completo = article;
                    if (string.IsNullOrWhiteSpace(completo.Image) && NeedsImage(completo.Link))
                    {
                        var og = await TryOgImageAsync(http, completo.Link);
                        if (!string.IsNullOrWhiteSpace(og))
                            completo = completo with { Image = og };
                    }
                    if (UpsertArticle(completo))
                        nuevos.Add(completo);
                }
                Console.WriteLine($"[OK] {feed.Name}: {validos} items revisados");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {feed.Name}: {Describe(ex)}");
            }
        }

        Console.WriteLine($"\nArtículos nuevos esta corrida: {nuevos.Count}");

        WriteNoticiasJson();

        if (args.Contains("--digest"))
            WriteDigest();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };

        var http = new HttpClient(handler)
        {
            Timeout = HttpTimeout,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml, */*;q=0.8");
        return http;
    }

    private static List<FeedSource> LoadFeeds()
    {
        var path = Path.Combine(_rootDir, FeedsConfigName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<FeedSource>>(json, JsonOptions) ?? new List<FeedSource>();
    }

    private static void InitDb()
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS Articles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Link TEXT NOT NULL UNIQUE,
                Source TEXT NOT NULL,
                PublishedAt TEXT,
                Summary TEXT,
                FetchedAt TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "Image");
    }

    private static void EnsureColumn(SqliteConnection conn, string column)
    {
        var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Articles') WHERE name = $name;";
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            return;
        var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE Articles ADD COLUMN {column} TEXT;";
        alter.ExecuteNonQuery();
    }

    private static async Task<List<Article>> FetchFeedWithRetryAsync(HttpClient http, FeedSource feed)
    {
        var urls = UrlsFor(feed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Exception? last = null;

        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    return await FetchFeedPagesAsync(http, feed, url);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    last = ex;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Console.WriteLine($"[RETRY] {feed.Name}: intento {attempt} falló ({Describe(ex)}). Reintento en {delay.TotalSeconds:0}s...");
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    last = ex;
                    break;
                }
            }
        }

        throw last ?? new InvalidOperationException("No se pudo leer el feed");
    }

    private static IEnumerable<string> UrlsFor(FeedSource feed)
    {
        yield return feed.Url;
        if (feed.Name.Contains("Motorpasión", StringComparison.OrdinalIgnoreCase)
            || feed.Url.Contains("motorpasion", StringComparison.OrdinalIgnoreCase))
        {
            yield return "https://www.motorpasion.com/feedburner.xml";
            yield return "https://www.motorpasion.com/feed/";
        }
    }

    private static async Task<byte[]> DownloadFeedBytesAsync(HttpClient http, string url)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var sniff = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 240)).TrimStart();
                var hint = LooksLikeHtml(sniff) ? " (devolvió HTML, posible bloqueo o URL rota)" : "";
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{hint}",
                    inner: null,
                    statusCode: response.StatusCode);
            }
            return bytes;
        }
        catch (Exception ex) when (IsSslFailure(ex))
        {
            Console.WriteLine($"[INFO] TLS de .NET falló para {url}; reintento con curl.");
            return await DownloadWithCurlAsync(url);
        }
    }

    private static async Task<byte[]> DownloadWithCurlAsync(string url)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo("curl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-sS");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("--compressed");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("25");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("%{http_code}");
            psi.ArgumentList.Add(url);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo ejecutar curl");
            var statusText = (await process.StandardOutput.ReadToEndAsync()).Trim();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"curl salió con {process.ExitCode}: {stderr}".Trim());

            if (!int.TryParse(statusText, out var status) || status < 200 || status >= 300)
                throw new HttpRequestException($"curl HTTP {statusText}", inner: null, statusCode: status is > 0 ? (HttpStatusCode)status : null);

            return await File.ReadAllBytesAsync(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temp */ }
        }
    }

    private static bool IsSslFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<List<Article>> FetchFeedPagesAsync(HttpClient http, FeedSource feed, string url)
    {
        var all = new List<Article>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= MaxFeedPages; page++)
        {
            var pageUrl = PagedUrl(url, page);
            List<Article> batch;
            try
            {
                batch = await ParseFeedPageAsync(http, feed, pageUrl);
            }
            catch (Exception) when (page > 1)
            {
                break;
            }

            var nuevosEnPagina = 0;
            foreach (var article in batch)
            {
                if (string.IsNullOrWhiteSpace(article.Link) || !seen.Add(article.Link))
                    continue;
                all.Add(article);
                nuevosEnPagina++;
            }

            if (batch.Count == 0 || (page > 1 && nuevosEnPagina == 0))
                break;
        }

        return all;
    }

    private static string PagedUrl(string url, int page)
    {
        if (page <= 1)
            return url;
        var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{sep}paged={page}";
    }

    private static async Task<List<Article>> ParseFeedPageAsync(HttpClient http, FeedSource feed, string url)
    {
        var bytes = await DownloadFeedBytesAsync(http, url);
        var sniff = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 240)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (LooksLikeHtml(sniff))
            throw new InvalidOperationException("El feed devolvió HTML en vez de RSS/Atom (posible bloqueo o redirect a una web)");

        var xml = DecodeXmlBytes(bytes);
        xml = SanitizeFeedXml(xml);

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            CheckCharacters = false,
            XmlResolver = null
        });

        var syndicationFeed = SyndicationFeed.Load(xmlReader)
            ?? throw new InvalidOperationException("SyndicationFeed.Load devolvió null");

        return syndicationFeed.Items.Select(item => MapItem(item, feed.Name)).ToList();
    }

    private static Article MapItem(SyndicationItem item, string source)
    {
        var title = CleanText(item.Title?.Text);
        if (string.IsNullOrWhiteSpace(title))
            title = "(sin título)";

        return new Article(
            Title: title,
            Link: GetLink(item),
            Source: source,
            PublishedAt: GetPublishedAt(item),
            Summary: GetSummary(item),
            Image: GetImage(item)
        );
    }

    private static string GetLink(SyndicationItem item)
    {
        var alternate = item.Links.FirstOrDefault(l =>
            string.Equals(l.RelationshipType, "alternate", StringComparison.OrdinalIgnoreCase));
        var uri = alternate?.Uri ?? item.Links.FirstOrDefault()?.Uri;
        if (uri != null)
            return uri.ToString();

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return item.Id.Trim();

        return "";
    }

    private static DateTimeOffset? GetPublishedAt(SyndicationItem item)
    {
        foreach (var candidate in new[] { item.PublishDate, item.LastUpdatedTime })
        {
            if (candidate.Year > 1970 && candidate < DateTimeOffset.UtcNow.AddYears(2))
                return candidate;
        }
        return null;
    }

    private static string GetSummary(SyndicationItem item)
    {
        var fromSummary = CleanText(item.Summary?.Text);
        if (!string.IsNullOrWhiteSpace(fromSummary))
            return Truncate(fromSummary, 400);

        if (item.Content is TextSyndicationContent textContent)
        {
            var fromContent = CleanText(textContent.Text);
            if (!string.IsNullOrWhiteSpace(fromContent))
                return Truncate(fromContent, 400);
        }

        foreach (var extension in item.ElementExtensions)
        {
            if (extension.OuterName is not ("encoded" or "description" or "content"))
                continue;
            try
            {
                var raw = extension.GetObject<string>();
                var cleaned = CleanText(raw);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return Truncate(cleaned, 400);
            }
            catch (Exception)
            {
                // extensiones mal formadas: se ignora y se sigue con el resto del item
            }
        }

        return "";
    }

    private static readonly Regex ImgSrcRegex = new(
        @"<img[^>]+src\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ImageSkip =
    {
        "scorecardresearch", "doubleclick", "google-analytics", "facebook.com/tr",
        "gravatar", "wp-smiley", "pixel.gif", "1x1", "/pixel", "tracking"
    };

    private static string GetImage(SyndicationItem item)
    {
        foreach (var link in item.Links)
        {
            var url = link.Uri?.ToString() ?? "";
            var media = link.MediaType ?? "";
            if (media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && IsUsefulImage(url))
                return url;
            if (string.Equals(link.RelationshipType, "enclosure", StringComparison.OrdinalIgnoreCase) && IsUsefulImage(url))
                return url;
        }

        foreach (var html in CollectHtmlBlobs(item))
        {
            foreach (Match match in ImgSrcRegex.Matches(html))
            {
                var url = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                if (IsUsefulImage(url))
                    return url;
            }
        }

        return "";
    }

    private static List<string> CollectHtmlBlobs(SyndicationItem item)
    {
        var blobs = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Summary?.Text))
            blobs.Add(item.Summary.Text);
        if (item.Content is TextSyndicationContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
            blobs.Add(textContent.Text);
        foreach (var extension in item.ElementExtensions)
        {
            if (extension.OuterName is not ("encoded" or "description" or "content" or "thumbnail"))
                continue;
            try
            {
                var raw = extension.GetObject<string>();
                if (!string.IsNullOrWhiteSpace(raw))
                    blobs.Add(raw);
            }
            catch (Exception)
            {
                // ignore
            }
        }
        return blobs;
    }

    private static bool IsUsefulImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;
        var lower = url.ToLowerInvariant();
        if (ImageSkip.Any(skip => lower.Contains(skip)))
            return false;
        var path = lower.Split('?', '#')[0];
        if (path.EndsWith(".svg") && lower.Contains("emoji"))
            return false;
        if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png")
            || path.EndsWith(".webp") || path.EndsWith(".avif") || path.EndsWith(".gif"))
            return true;
        if (lower.Contains("wp-content/uploads") || lower.Contains("/imagenes/")
            || lower.Contains("resizer") || lower.Contains("i.blogs.es"))
            return true;
        return false;
    }

    private static bool NeedsImage(string link)
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Image FROM Articles WHERE Link = $link;";
        cmd.Parameters.AddWithValue("$link", link);
        var value = cmd.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value);
    }

    private static async Task<string> TryOgImageAsync(HttpClient http, string articleUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await http.GetAsync(articleUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return "";
            var html = await response.Content.ReadAsStringAsync(cts.Token);
            if (html.Length > 120_000)
                html = html[..120_000];
            return ExtractOgImage(html);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string ExtractOgImage(string html)
    {
        var patterns = new[]
        {
            @"<meta[^>]+property\s*=\s*[""']og:image[""'][^>]*content\s*=\s*[""']([^""']+)[""']",
            @"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*property\s*=\s*[""']og:image[""']",
            @"<meta[^>]+name\s*=\s*[""']twitter:image[""'][^>]*content\s*=\s*[""']([^""']+)[""']"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            var url = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (IsUsefulImage(url))
                return url;
        }
        return "";
    }

    private static bool UpsertArticle(Article article)
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT OR IGNORE INTO Articles (Title, Link, Source, PublishedAt, Summary, Image, FetchedAt)
              VALUES ($title, $link, $source, $publishedAt, $summary, $image, $fetchedAt);";
        cmd.Parameters.AddWithValue("$title", article.Title);
        cmd.Parameters.AddWithValue("$link", article.Link);
        cmd.Parameters.AddWithValue("$source", article.Source);
        cmd.Parameters.AddWithValue("$publishedAt", article.PublishedAt?.ToString("O") ?? "");
        cmd.Parameters.AddWithValue("$summary", article.Summary);
        cmd.Parameters.AddWithValue("$image", article.Image);
        cmd.Parameters.AddWithValue("$fetchedAt", DateTimeOffset.UtcNow.ToString("O"));
        var isNew = cmd.ExecuteNonQuery() > 0;

        if (!string.IsNullOrWhiteSpace(article.Image))
        {
            var upd = conn.CreateCommand();
            upd.CommandText =
                @"UPDATE Articles SET Image = $image
                  WHERE Link = $link AND (Image IS NULL OR Image = '');";
            upd.Parameters.AddWithValue("$image", article.Image);
            upd.Parameters.AddWithValue("$link", article.Link);
            upd.ExecuteNonQuery();
        }

        return isNew;
    }

    private static void WriteDigest()
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT Title, Link, Source, PublishedAt, Summary
              FROM Articles
              WHERE FetchedAt >= $since
              ORDER BY CASE WHEN PublishedAt IS NULL OR PublishedAt = '' THEN 1 ELSE 0 END,
                       PublishedAt DESC;";
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-7).ToString("O"));

        var items = ReadArticles(cmd).Select(a => new
        {
            title = a.Title,
            link = a.Link,
            source = a.Source,
            publishedAt = a.PublishedAt?.ToString("O") ?? "",
            summary = a.Summary
        }).ToList();

        var digestPath = Path.Combine(_rootDir, $"digest_{DateTime.Now:yyyy-MM-dd}.json");
        File.WriteAllText(digestPath, JsonSerializer.Serialize(items, JsonOptions));
        Console.WriteLine($"Digest guardado en {digestPath} ({items.Count} artículos de los últimos 7 días)");
    }

    private static void WriteNoticiasJson()
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT Title, Link, Source, PublishedAt, Summary, Image
              FROM Articles
              ORDER BY CASE WHEN PublishedAt IS NULL OR PublishedAt = '' THEN 1 ELSE 0 END,
                       PublishedAt DESC;";

        var items = ReadArticles(cmd).Select(a => new
        {
            titulo = a.Title,
            link = a.Link,
            fuente = a.Source,
            fecha = FormatFecha(a.PublishedAt),
            resumen = a.Summary,
            imagen = a.Image
        }).ToList();

        var docsDir = Path.Combine(_rootDir, "docs");
        Directory.CreateDirectory(docsDir);
        var path = Path.Combine(docsDir, "noticias.json");
        File.WriteAllText(path, JsonSerializer.Serialize(items, JsonOptions));
        Console.WriteLine($"Noticias para el sitio: {path} ({items.Count} artículos)");
    }

    private static List<Article> ReadArticles(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var items = new List<Article>();
        while (reader.Read())
        {
            DateTimeOffset? published = null;
            var rawDate = reader.IsDBNull(3) ? "" : reader.GetString(3);
            if (DateTimeOffset.TryParse(rawDate, out var parsed))
                published = parsed;

            items.Add(new Article(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                published,
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : ""
            ));
        }
        return items;
    }

    private static SqliteConnection OpenDb()
    {
        var conn = new SqliteConnection($"Data Source={Path.Combine(_rootDir, DbFileName)}");
        conn.Open();
        return conn;
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, FeedsConfigName)))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName ?? "";
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static bool LooksLikeHtml(string sniff) =>
        sniff.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
        || sniff.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase);

    private static string DecodeXmlBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string SanitizeFeedXml(string xml)
    {
        xml = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        xml = Regex.Replace(xml, @"<pubDate>\s*</pubDate>", "", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, @"<updated>\s*</updated>", "", RegexOptions.IgnoreCase);
        xml = Regex.Replace(xml, @"<dc:date>\s*</dc:date>", "", RegexOptions.IgnoreCase);
        xml = xml.Replace("\0", "");
        return xml;
    }

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var stripped = Regex.Replace(text, "<[^>]+>", " ");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
        return stripped;
    }

    private static string FormatFecha(DateTimeOffset? published) =>
        published?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "";

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf(' ', max);
        if (cut < max / 2)
            cut = max;
        return text[..cut].TrimEnd() + "…";
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException or IOException or AuthenticationException)
            return true;
        if (ex is HttpRequestException httpEx)
        {
            var code = (int?)httpEx.StatusCode;
            if (code is null)
                return true;
            return code >= 500 || code is 408 or 429;
        }
        return ex.InnerException != null && IsTransient(ex.InnerException);
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (ex.InnerException != null && !msg.Contains(ex.InnerException.Message, StringComparison.Ordinal))
            msg += $" ({ex.InnerException.Message})";
        return msg;
    }
}
