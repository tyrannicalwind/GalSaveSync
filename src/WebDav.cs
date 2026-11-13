using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using System.Xml;
using System.Linq;
using System.Collections.Generic;

namespace GalSaveSync {
    public interface ICloudStore : IDisposable {
        RemoteObject Get(string path);
        bool Put(string path, byte[] data, string etag, bool createOnly);
        void EnsureDirectory(string path);
        List<string> Directories(string path);
        List<string> Files(string path);
    }
    public sealed class WebDavStore : ICloudStore {
        readonly HttpClient client;
        readonly Uri root;
        const long MaxDownload = 512L * 1024 * 1024;
        public WebDavStore(string url, string username, string password) {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || !String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment) || !String.IsNullOrEmpty(parsed.UserInfo))
                throw new Exception("请输入不带查询参数、片段或内嵌密码的 WebDAV 文件夹地址。");
            if (parsed.Scheme != "https" && parsed.Scheme != "http") throw new Exception("WebDAV 地址必须使用 HTTP 或 HTTPS。");
            root = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // This client accepts all server certificates, as requested; no Windows trust-store changes.
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            client = new HttpClient(handler); client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((username ?? "") + ":" + (password ?? ""))));
        }
        Uri Url(string path) { return new Uri(root, String.Join("/", path.Split('/').Select(Uri.EscapeDataString))); }
        HttpResponseMessage Send(string method, string path, byte[] data, IDictionary<string, string> headers) {
            using (var request = new HttpRequestMessage(new HttpMethod(method), Url(path))) {
                if (data != null) request.Content = new ByteArrayContent(data);
                if (headers != null) foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                HttpResponseMessage response;
                try { response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
                catch (HttpRequestException ex) { throw new Exception(DescribeTransportFailure(ex, method, request.RequestUri), ex); }
                catch (TaskCanceledException ex) { throw new Exception(DescribeTransportFailure(ex, method, request.RequestUri), ex); }
                int code = (int)response.StatusCode;
                if (code >= 300 && code < 400) { response.Dispose(); throw new Exception("WebDAV 返回重定向。请填写最终文件夹地址（检查末尾斜杠），程序不会向重定向地址发送密码。"); }
                return response;
            }
        }
        internal static string DescribeTransportFailure(Exception error, string method, Uri target) {
            WebException web = null; SocketException socket = null; Exception deepest = error;
            bool tls = false;
            for (Exception current = error; current != null; current = current.InnerException) {
                deepest = current;
                if (current is WebException) web = (WebException)current;
                if (current is SocketException) socket = (SocketException)current;
                if (current is System.Security.Authentication.AuthenticationException) tls = true;
            }
            string reason = "连接中断，请检查服务器、反向代理或系统代理设置。";
            if (error is TaskCanceledException || (web != null && web.Status == WebExceptionStatus.Timeout)) reason = "连接超时，请检查地址、端口和网络是否可达。";
            else if (web != null && (web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProxyNameResolutionFailure)) reason = "域名或代理地址解析失败，请检查域名和系统代理。";
            else if (tls || (web != null && (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure))) reason = "HTTPS / TLS 握手失败，请检查服务是否支持 HTTPS，以及协议和端口是否匹配。程序已忽略证书验证。";
            else if (socket != null && socket.SocketErrorCode == SocketError.ConnectionRefused) reason = "服务器拒绝连接，请检查端口及 WebDAV 服务是否已启动。";
            else if (web != null && web.Status == WebExceptionStatus.ConnectFailure) reason = "无法连接服务器，请检查地址、端口、防火墙和系统代理。";
            string code = web == null ? error.GetType().Name : web.Status.ToString();
            if (socket != null) code += " / Socket " + socket.NativeErrorCode;
            // Never include request headers, credentials, query strings or userinfo.
            string endpoint = target.Scheme + "://" + target.Authority;
            return "WebDAV " + method + " 连接失败：" + endpoint + Environment.NewLine + reason + Environment.NewLine + "原因 [" + code + "]：" + deepest.Message;
        }
        static void Check(HttpResponseMessage response, string method) {
            if (response.IsSuccessStatusCode) return;
            throw new Exception("WebDAV " + method + " 失败，HTTP " + (int)response.StatusCode + (response.StatusCode == HttpStatusCode.Unauthorized ? "：请检查账号密码。" : "：请检查地址、权限和服务器配置。"));
        }
        static byte[] ReadBody(HttpResponseMessage response) {
            if (response.Content.Headers.ContentLength > MaxDownload) throw new Exception("远端文件超过 512 MiB，已停止下载。");
            using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var output = new MemoryStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                    if (output.Length + n > MaxDownload) throw new Exception("远端文件超过 512 MiB，已停止下载。");
                    output.Write(buffer, 0, n);
                }
                return output.ToArray();
            }
        }
        public RemoteObject Get(string path) {
            using (var response = Send("GET", path, null, new Dictionary<string,string> { { "Cache-Control", "no-cache" } })) {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                Check(response, "GET");
                return new RemoteObject { Data = ReadBody(response), ETag = response.Headers.ETag == null ? null : response.Headers.ETag.ToString() };
            }
        }
        public bool Put(string path, byte[] data, string etag, bool createOnly) {
            var headers = new Dictionary<string,string>();
            if (createOnly) headers.Add("If-None-Match", "*");
            else {
                if (String.IsNullOrEmpty(etag) || etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) throw new Exception("WebDAV 没有提供强 ETag，无法安全更新版本索引。请换用支持 ETag 和条件写入的 WebDAV 服务。");
                headers.Add("If-Match", etag);
            }
            using (var response = Send("PUT", path, data, headers)) {
                if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
                Check(response, "PUT"); return true;
            }
        }
        public void EnsureDirectory(string path) {
            string current = "";
            foreach (var part in path.Trim('/').Split('/')) {
                current += part + "/";
                using (var response = Send("MKCOL", current, null, null)) {
                    if (response.StatusCode == HttpStatusCode.MethodNotAllowed) {
                        // A 405 alone does not prove that the resource is a collection.
                        Directories(current); continue;
                    }
                    Check(response, "MKCOL");
                }
            }
        }
        public List<string> Directories(string path) {
            return ListEntries(path, true);
        }
        public List<string> Files(string path) { return ListEntries(path, false); }
        List<string> ListEntries(string path, bool directories) {
            byte[] xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>");
            using (var response = Send("PROPFIND", path.TrimEnd('/') + "/", xml, new Dictionary<string,string>{{"Depth", "1"}})) {
                if (!directories && response.StatusCode == HttpStatusCode.NotFound) return new List<string>();
                Check(response, "PROPFIND");
                var document = new XmlDocument { XmlResolver = null };
                using (var stream = new MemoryStream(ReadBody(response)))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("d", "DAV:");
                var names = new List<string>();
                string parent = Url(path.TrimEnd('/') + "/").AbsolutePath.TrimEnd('/') + "/";
                foreach (XmlNode item in document.SelectNodes("//d:response", ns)) {
                    var property = item.SelectSingleNode("d:propstat[contains(d:status,' 200 ')]/d:prop/d:resourcetype", ns);
                    if (property == null || (property.SelectSingleNode("d:collection", ns) != null) != directories) continue;
                    var href = item.SelectSingleNode("d:href", ns); if (href == null) continue;
                    Uri child; if (!Uri.TryCreate(root, href.InnerText, out child) || child.Authority != root.Authority) continue;
                    string childPath = child.AbsolutePath.TrimEnd('/');
                    if (!childPath.StartsWith(parent, StringComparison.Ordinal)) continue;
                    string name = childPath.Substring(parent.Length);
                    if (name.Length > 0 && !name.Contains('/')) names.Add(Uri.UnescapeDataString(name));
                }
                return names;
            }
        }
        public void TestConnection() {
            string path = "GalgameSaves/probes/" + Guid.NewGuid().ToString("N") + ".json";
            byte[] first = Encoding.UTF8.GetBytes("{\"probe\":1}");
            bool created = false;
            try {
                EnsureDirectory("GalgameSaves/games"); EnsureDirectory("GalgameSaves/probes");
                if (!Put(path, first, null, true)) throw new Exception("无法创建测试文件。");
                created = true;
                var before = Get(path);
                if (before == null || !before.Data.SequenceEqual(first)) throw new Exception("WebDAV 上传和下载不一致。");
                if (!Files("GalgameSaves/probes").Contains(path.Substring(path.LastIndexOf('/') + 1))) throw new Exception("WebDAV 目录列表未返回刚上传的文件，请稍后重试或检查 PROPFIND 支持。");
            } finally { if (created) try { using (var r = Send("DELETE", path, null, null)) {} } catch {} }
        }
        public void Dispose() { client.Dispose(); }
    }
}
