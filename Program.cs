using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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

public record WeekHighlight(
    string Titulo,
    string Link,
    string Fuente,
    string Fecha,
    string Resumen,
    string Imagen,
    int Menciones,
    List<string> Fuentes);

public record GuionIndexItem(string fecha, string rango, string esqueleto, string? guion = null);

public record PublishedItem(string? titulo, string? link, string? fuente, string? fecha, string? resumen, string? imagen);

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
        LoadDotEnv();
        InitDb();
        ImportPublishedNoticias();

        if (args.Contains("--sitio"))
        {
            await ServeSitioAsync();
            return;
        }

        if (args.Contains("--guion-ia"))
        {
            var fechaIa = ArgValue(args, "--guion-ia") ?? DateTime.Now.ToString("yyyy-MM-dd");
            await CrearGuionGptAsync(fechaIa);
            return;
        }

        if (args.Contains("--guion-only"))
        {
            WriteGuionBorrador(WriteSemanaJson());
            return;
        }

        var feeds = LoadFeeds();

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
        var semana = WriteSemanaJson();

        if (args.Contains("--digest") || DateTime.Now.DayOfWeek == DayOfWeek.Friday)
            WriteDigest();

        if (args.Contains("--guion") || DateTime.Now.DayOfWeek == DayOfWeek.Friday)
            WriteGuionBorrador(semana);
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
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-AR,es;q=0.9,en;q=0.8");
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

    private static void ImportPublishedNoticias()
    {
        var path = Path.Combine(_rootDir, "docs", "noticias.json");
        if (!File.Exists(path))
            return;

        List<PublishedItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<PublishedItem>>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return;
        }

        if (items is null || items.Count == 0)
            return;

        var inserted = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.link))
                continue;
            DateTimeOffset? published = null;
            if (!string.IsNullOrWhiteSpace(item.fecha) && DateTimeOffset.TryParse(item.fecha, out var parsed))
                published = parsed;
            if (UpsertArticle(new Article(
                    item.titulo ?? "",
                    item.link,
                    item.fuente ?? "",
                    published,
                    item.resumen ?? "",
                    item.imagen ?? "")))
                inserted++;
        }

        if (inserted > 0)
            Console.WriteLine($"Recuperadas {inserted} notas ya publicadas (por si un feed falla hoy).");
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
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                req.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");
            using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Console.WriteLine($"[INFO] HTTP 403 para {url}; reintento con curl.");
                    return await DownloadWithCurlAsync(url);
                }
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
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Accept: application/rss+xml, application/xml, text/xml, */*;q=0.8");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Accept-Language: es-AR,es;q=0.9");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"Referer: {uri.Scheme}://{uri.Host}/");
            }
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

    private static readonly HashSet<string> TitleStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "el","la","los","las","un","una","unos","unas","de","del","al","y","o","en","a","por","para",
        "con","su","sus","que","se","es","son","fue","como","más","mas","ya","hoy","sobre","todo",
        "esta","este","estos","estas","the","and","for","with"
    };

    private static List<WeekHighlight> WriteSemanaJson()
    {
        using var conn = OpenDb();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT Title, Link, Source, PublishedAt, Summary, Image
              FROM Articles
              WHERE PublishedAt >= $since
              ORDER BY PublishedAt DESC;";
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-7).ToString("O"));

        var week = ReadArticles(cmd);
        var ranked = RankWeekHighlights(week);
        var docsDir = Path.Combine(_rootDir, "docs");
        Directory.CreateDirectory(docsDir);
        var payload = new
        {
            actualizado = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            desde = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-dd"),
            hasta = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
            destacadas = ranked.Select(h => new
            {
                titulo = h.Titulo,
                link = h.Link,
                fuente = h.Fuente,
                fecha = h.Fecha,
                resumen = h.Resumen,
                imagen = h.Imagen,
                menciones = h.Menciones,
                fuentes = h.Fuentes
            })
        };
        var path = Path.Combine(docsDir, "semana.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
        Console.WriteLine($"Resumen semanal: {path} ({ranked.Count} temas destacados de {week.Count} notas)");
        return ranked;
    }

    private static List<WeekHighlight> RankWeekHighlights(List<Article> week)
    {
        var clusters = new List<List<Article>>();
        foreach (var article in week)
        {
            var tokens = TitleTokens(article.Title);
            var host = clusters.FirstOrDefault(c =>
                c.Any(existing => SameStory(TitleTokens(existing.Title), tokens)));
            if (host is null)
                clusters.Add(new List<Article> { article });
            else
                host.Add(article);
        }

        return clusters
            .Select(cluster =>
            {
                var lead = cluster
                    .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
                    .ThenByDescending(a => a.Summary.Length)
                    .First();
                var fuentes = cluster.Select(a => a.Source).Distinct().OrderBy(s => s).ToList();
                var recency = lead.PublishedAt ?? DateTimeOffset.UtcNow;
                var score = (fuentes.Count - 1) * 100 + Math.Max(0, 168 - (DateTimeOffset.UtcNow - recency).TotalHours);
                return new
                {
                    score,
                    item = new WeekHighlight(
                        lead.Title,
                        lead.Link,
                        lead.Source,
                        FormatFecha(lead.PublishedAt),
                        lead.Summary,
                        lead.Image,
                        cluster.Count,
                        fuentes)
                };
            })
            .OrderByDescending(x => x.score)
            .Take(24)
            .Select(x => x.item)
            .ToList();
    }

    private static readonly string[] LocalHints =
    {
        "argentina", "argentin", "córdoba", "cordoba", "buenos aires",
        "banco nación", "patentamiento", "papamóvil", "papamovil"
    };

    private static readonly string[] ForeignHints =
    {
        "méxico", "mexico", "españa", "europa", "ee.uu", "eeuu",
        "estados unidos", "china", "alemania", "francia", "italia"
    };

    private static bool IsSpanishFeed(string source) =>
        source.Contains("(ES)", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Motorpasión", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Diariomotor", StringComparison.OrdinalIgnoreCase);

    private static bool IsInternacional(WeekHighlight h)
    {
        if (IsSpanishFeed(h.Fuente) || h.Fuentes.Any(IsSpanishFeed))
            return true;

        var blob = $"{h.Titulo} {h.Resumen}".ToLowerInvariant();
        if (LocalHints.Any(blob.Contains))
            return false;
        return ForeignHints.Any(blob.Contains);
    }

    private static void WriteGuionBorrador(List<WeekHighlight> destacadas)
    {
        var nacional = destacadas.Where(h => !IsInternacional(h)).Take(5).ToList();
        var internacional = destacadas.Where(IsInternacional).Take(4).ToList();
        if (internacional.Count == 0)
            internacional = destacadas.Except(nacional).Take(4).ToList();

        var rango = EtiquetaViernes(DateTime.Now);
        var gancho = nacional.FirstOrDefault() ?? destacadas.FirstOrDefault();
        var sb = new StringBuilder();

        sb.AppendLine($"# Punto muerto — guion {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"Semana: {rango}");
        sb.AppendLine("Borrador para refinar a mano. [OPINIÓN], [DATO] y [CTA] los completan los conductores.");
        sb.AppendLine();
        sb.AppendLine("## HOOK — 30 segundos");
        sb.AppendLine();
        if (gancho is null)
        {
            sb.AppendLine("- [IA] Idea potente / pregunta / anécdota.");
        }
        else
        {
            sb.AppendLine($"- [IA] Pregunta o anécdota a partir de: {gancho.Titulo}");
            sb.AppendLine($"  {gancho.Resumen}");
        }
        sb.AppendLine();
        sb.AppendLine("## INTRO — 1 minuto");
        sb.AppendLine();
        sb.AppendLine("- [IA] Qué vamos a hablar y por qué importa.");
        if (nacional.Count > 0)
            sb.AppendLine($"- Nacional: {string.Join("; ", nacional.Take(3).Select(h => h.Titulo))}.");
        if (internacional.Count > 0)
            sb.AppendLine($"- Internacional: {string.Join("; ", internacional.Take(2).Select(h => h.Titulo))}.");
        sb.AppendLine();
        sb.AppendLine("## BLOQUE 1 — Nacional");
        sb.AppendLine();
        sb.AppendLine("### Idea principal");
        sb.AppendLine();
        sb.AppendLine(nacional.Count > 0
            ? $"- [IA] {nacional[0].Titulo}"
            : "- [IA] Idea principal de la semana local.");
        sb.AppendLine();
        sb.AppendLine("### Noticias más importantes");
        sb.AppendLine();
        AppendNotas(sb, nacional);
        sb.AppendLine();
        sb.AppendLine("### Opinión personal");
        sb.AppendLine();
        sb.AppendLine("- [OPINIÓN]");
        sb.AppendLine();
        sb.AppendLine("### Dato que quiero mencionar");
        sb.AppendLine();
        sb.AppendLine("- [DATO]");
        sb.AppendLine();
        sb.AppendLine("## BLOQUE 2 — Internacional");
        sb.AppendLine();
        sb.AppendLine("### Intro");
        sb.AppendLine();
        sb.AppendLine("- [IA] Puente desde Argentina hacia lo que pasó afuera.");
        sb.AppendLine();
        sb.AppendLine("### Lo más relevante de la semana");
        sb.AppendLine();
        AppendNotas(sb, internacional);
        sb.AppendLine();
        sb.AppendLine("### Opinión personal");
        sb.AppendLine();
        sb.AppendLine("- [OPINIÓN]");
        sb.AppendLine();
        sb.AppendLine("### Contrapunto / diferencias con Argentina");
        sb.AppendLine();
        sb.AppendLine("- [IA] Qué de esto llega, no llega o llega distinto acá.");
        sb.AppendLine("- [OPINIÓN]");
        sb.AppendLine();
        sb.AppendLine("## BLOQUE 3 — Conclusión");
        sb.AppendLine();
        sb.AppendLine("- [IA] Qué me llevo de la semana (ideas, 3 o 4 líneas).");
        sb.AppendLine("- [OPINIÓN]");
        sb.AppendLine();
        sb.AppendLine("## CIERRE");
        sb.AppendLine();
        sb.AppendLine("- [IA] Frase final.");
        sb.AppendLine("- [CTA]");

        var fecha = DateTime.Now.ToString("yyyy-MM-dd");
        var borrador = sb.ToString();
        var path = Path.Combine(_rootDir, $"guion_{fecha}.md");
        File.WriteAllText(path, borrador);
        Console.WriteLine($"Esqueleto: {path} ({nacional.Count} nacionales, {internacional.Count} internacionales)");
        PublicarEsqueleto(fecha, rango, borrador);
    }

    private static async Task<string?> CompletarGuionConIaAsync(string borrador)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("Sin OPENAI_API_KEY en .env.");
            return null;
        }

        var promptPath = Path.Combine(_rootDir, "guion", "prompt.txt");
        var sistema = File.Exists(promptPath)
            ? File.ReadAllText(promptPath)
            : "Completá el guion de Punto muerto. No toques [OPINIÓN], [DATO] ni [CTA].";
        var modelo = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(modelo))
            modelo = "gpt-4o-mini";

        var payload = new
        {
            model = modelo,
            temperature = 0.7,
            messages = new[]
            {
                new { role = "system", content = sistema },
                new
                {
                    role = "user",
                    content = "Completá solo los ítems [IA] como borrador de guion (texto escrito, no audio). Dejá intactos [OPINIÓN], [DATO] y [CTA]. Devolvé solo el markdown, sin fences.\n\n" + borrador
                }
            }
        };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WARN] OpenAI {((int)res.StatusCode)}: {Truncate(json, 280)}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
                return null;

            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var firstNl = content.IndexOf('\n');
                if (firstNl > 0)
                    content = content[(firstNl + 1)..];
                if (content.EndsWith("```"))
                    content = content[..^3].TrimEnd();
            }
            return content.Trim() + "\n";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] No se pudo llamar a la IA: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> CrearGuionGptAsync(string fecha)
    {
        var password = Environment.GetEnvironmentVariable("PUNTO_PODCAST_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Falta PUNTO_PODCAST_PASSWORD en .env.");

        var lista = ReadGuionIndex();
        var item = lista.FirstOrDefault(x => x.fecha == fecha);
        if (item is null || string.IsNullOrWhiteSpace(item.esqueleto))
            throw new InvalidOperationException($"No hay esqueleto para {fecha}. Primero corre dotnet run -- --guion-only.");

        if (!string.IsNullOrWhiteSpace(item.guion) && File.Exists(Path.Combine(_rootDir, "docs", item.guion)))
        {
            Console.WriteLine($"Ya existe guion GPT para {fecha}; no se vuelve a llamar a OpenAI.");
            return DecryptGuion(File.ReadAllText(Path.Combine(_rootDir, "docs", item.guion)), password);
        }

        var packed = File.ReadAllText(Path.Combine(_rootDir, "docs", item.esqueleto));
        var esqueleto = DecryptGuion(packed, password);
        var texto = await CompletarGuionConIaAsync(esqueleto);
        if (string.IsNullOrWhiteSpace(texto))
            throw new InvalidOperationException("OpenAI no devolvió un guion. Revisá créditos o la API key.");

        var rel = $"guiones/{fecha}-guion.json";
        Directory.CreateDirectory(Path.Combine(_rootDir, "docs", "guiones"));
        File.WriteAllText(Path.Combine(_rootDir, "docs", rel), JsonSerializer.Serialize(EncryptGuion(texto, password), JsonOptions));
        File.WriteAllText(Path.Combine(_rootDir, $"guion_{fecha}.md"), texto);

        var idx = lista.FindIndex(x => x.fecha == fecha);
        lista[idx] = item with { guion = rel };
        WriteGuionIndex(lista);
        Console.WriteLine($"Guion GPT guardado en docs/{rel}");
        return texto;
    }

    private static void PublicarEsqueleto(string fecha, string rango, string texto)
    {
        var password = Environment.GetEnvironmentVariable("PUNTO_PODCAST_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("Sin PUNTO_PODCAST_PASSWORD en .env: el esqueleto no se sube a /podcast.html.");
            return;
        }

        var rel = $"guiones/{fecha}-esqueleto.json";
        Directory.CreateDirectory(Path.Combine(_rootDir, "docs", "guiones"));
        File.WriteAllText(
            Path.Combine(_rootDir, "docs", rel),
            JsonSerializer.Serialize(EncryptGuion(texto, password), JsonOptions));

        var lista = ReadGuionIndex();
        var prev = lista.FirstOrDefault(x => x.fecha == fecha);
        lista.RemoveAll(x => x.fecha == fecha);
        lista.Add(new GuionIndexItem(fecha, rango, rel, prev?.guion));
        WriteGuionIndex(lista);
        Console.WriteLine($"Esqueleto publicado (cifrado) en docs/{rel}");
    }

    private static List<GuionIndexItem> ReadGuionIndex()
    {
        var path = Path.Combine(_rootDir, "docs", "guiones.json");
        var lista = new List<GuionIndexItem>();
        if (!File.Exists(path))
            return lista;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return lista;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var fecha = el.TryGetProperty("fecha", out var f) ? f.GetString() ?? "" : "";
            var rango = el.TryGetProperty("rango", out var r) ? r.GetString() ?? "" : "";
            var esq = el.TryGetProperty("esqueleto", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(esq) && el.TryGetProperty("archivo", out var a))
                esq = a.GetString();
            string? guion = null;
            if (el.TryGetProperty("guion", out var g) && g.ValueKind == JsonValueKind.String)
                guion = g.GetString();
            if (string.IsNullOrWhiteSpace(guion))
                guion = null;
            if (!string.IsNullOrWhiteSpace(fecha))
                lista.Add(new GuionIndexItem(fecha, rango, esq ?? "", guion));
        }
        return lista;
    }

    private static void WriteGuionIndex(List<GuionIndexItem> lista)
    {
        var path = Path.Combine(_rootDir, "docs", "guiones.json");
        var ordered = lista.OrderByDescending(x => x.fecha).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(ordered, JsonOptions));
    }

    private static string DecryptGuion(string packedJson, string password)
    {
        using var doc = JsonDocument.Parse(packedJson);
        var root = doc.RootElement;
        var salt = Convert.FromBase64String(root.GetProperty("salt").GetString() ?? "");
        var iv = Convert.FromBase64String(root.GetProperty("iv").GetString() ?? "");
        var data = Convert.FromBase64String(root.GetProperty("ct").GetString() ?? "");
        if (data.Length < 16)
            throw new InvalidOperationException("Archivo de guion inválido.");

        var cipher = data[..^16];
        var tag = data[^16..];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length)
            return null;
        var next = args[i + 1];
        return next.StartsWith("--") ? null : next;
    }

    private static async Task ServeSitioAsync()
    {
        const string prefix = "http://127.0.0.1:8080/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Prefixes.Add("http://localhost:8080/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            Console.WriteLine("No se pudo abrir el puerto 8080. Cerrá el otro servidor local e intentá de nuevo.");
            return;
        }

        Console.WriteLine($"Sitio en {prefix}  (Ctrl+C para cortar)");
        Console.WriteLine($"Podcast: {prefix}podcast.html");
        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleSitioRequestAsync(ctx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[sitio] {ex.Message}");
                    try
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            });
        }
    }

    private static async Task HandleSitioRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/");

        if (req.HttpMethod == "POST" && path.Equals("/api/crear-guion", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            string fecha = DateTime.Now.ToString("yyyy-MM-dd");
            string password = "";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("fecha", out var f))
                    fecha = f.GetString() ?? fecha;
                if (doc.RootElement.TryGetProperty("password", out var p))
                    password = p.GetString() ?? "";
            }
            catch
            {
                await WriteJsonAsync(res, 400, new { ok = false, error = "Pedido inválido." });
                return;
            }

            var expected = Environment.GetEnvironmentVariable("PUNTO_PODCAST_PASSWORD") ?? "";
            if (string.IsNullOrWhiteSpace(expected) || password != expected)
            {
                await WriteJsonAsync(res, 401, new { ok = false, error = "Contraseña incorrecta." });
                return;
            }

            try
            {
                var lista = ReadGuionIndex();
                var item = lista.FirstOrDefault(x => x.fecha == fecha);
                var already = item is not null && !string.IsNullOrWhiteSpace(item.guion)
                    && File.Exists(Path.Combine(_rootDir, "docs", item.guion));
                var markdown = await CrearGuionGptAsync(fecha);
                var after = ReadGuionIndex().FirstOrDefault(x => x.fecha == fecha);
                await WriteJsonAsync(res, 200, new { ok = true, already, guion = after?.guion, markdown });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(res, 502, new { ok = false, error = ex.Message });
            }
            return;
        }

        if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD")
        {
            res.StatusCode = 405;
            res.Close();
            return;
        }

        var docs = Path.Combine(_rootDir, "docs");
        var relative = path == "/" ? "index.html" : path.TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(docs, relative));
        var root = Path.GetFullPath(docs);
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        res.ContentType = MimeFor(full);
        var bytes = await File.ReadAllBytesAsync(full);
        res.ContentLength64 = bytes.Length;
        if (req.HttpMethod == "GET")
            await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static object EncryptGuion(string plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, plain, cipher, tag);
        var packed = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, packed, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, cipher.Length, tag.Length);
        return new
        {
            v = 1,
            salt = Convert.ToBase64String(salt),
            iv = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(packed)
        };
    }

    private static void LoadDotEnv()
    {
        var path = Path.Combine(_rootDir, ".env");
        if (!File.Exists(path))
        {
            Console.WriteLine("No encontré .env en la raíz del proyecto.");
            return;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
            if (!string.IsNullOrWhiteSpace(value))
                Environment.SetEnvironmentVariable(key, value);
        }

        var loaded = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        Console.WriteLine(loaded
            ? "OPENAI_API_KEY cargada desde .env"
            : "OPENAI_API_KEY sigue vacía en .env");
    }

    private static void AppendNotas(StringBuilder sb, List<WeekHighlight> notas)
    {
        if (notas.Count == 0)
        {
            sb.AppendLine("- (sin notas en este bloque esta semana)");
            return;
        }

        foreach (var h in notas)
        {
            var medios = h.Fuentes.Count > 0 ? string.Join(", ", h.Fuentes) : h.Fuente;
            sb.AppendLine($"- **{h.Titulo}**");
            sb.AppendLine($"  {h.Resumen}");
            sb.AppendLine($"  {medios} · {h.Link}");
        }
    }

    private static HashSet<string> TitleTokens(string title)
    {
        var words = Regex.Split(title.ToLowerInvariant(), @"[^\p{L}0-9]+")
            .Where(w => w.Length > 2 && !TitleStops.Contains(w));
        return new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameStory(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return false;
        var inter = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        if (inter >= 4) return true;
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union > 0 && (double)inter / union >= 0.4;
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

    private static string EtiquetaViernes(DateTime day)
    {
        var cultura = new CultureInfo("es-AR");
        var raw = day.ToString("dddd d 'de' MMMM", cultura);
        if (string.IsNullOrEmpty(raw))
            return day.ToString("yyyy-MM-dd");
        return char.ToUpper(raw[0], cultura) + raw[1..];
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
