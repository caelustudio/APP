Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' 登录成功后拿到的 Star ID 账号信息。字段取决于授权范围（scope）。
''' </summary>
Public Class StarIDUser
    ''' <summary>Star ID 唯一编号，例如 CS-A1B2C3D4（默认授予）</summary>
    Public Property StarId As String = ""
    ''' <summary>昵称（需要 profile）</summary>
    Public Property Nickname As String = ""
    ''' <summary>邮箱（需要 email）</summary>
    Public Property Email As String = ""
    Public Property EmailVerified As Boolean = False
    ''' <summary>账号创建时间，毫秒时间戳（需要 profile）</summary>
    Public Property CreatedAt As Long = 0
End Class

''' <summary>
''' Caelus Star ID 登录（OAuth 2.0 授权码模式 + PKCE）。
'''
''' 桌面应用属于「公开客户端」：没法安全保存 client_secret，所以：
'''   - 必须走 PKCE（S256），不传 client_secret
'''   - 回调用本地回环地址 http://127.0.0.1:&lt;端口&gt;/callback，
'''     流程里临时起一个 TcpListener 接授权码，拿到就关掉
''' 回调地址必须和登记时填的完全一致（含协议、路径、大小写）。
''' </summary>
Public Module StarIDSource

    ' ==================== 配置 ====================

    Private Const BaseUrl As String = "https://caelus-terminal.app.workbuddy.host"

    ''' <summary>回调地址默认值；用户可在「Star ID 配置」里改</summary>
    Public Const DefaultRedirectUri As String = "http://127.0.0.1:57321/callback"

    ''' <summary>
    ''' 内置客户端 ID（管理员在终端控制台登记应用后生成，2026-10-04 以登记页为准更新）。
    ''' 桌面应用是公开客户端、走 PKCE 不传密钥，内置 ID 不会造成密钥泄露。
    ''' 用户无需自己填：starid.txt 里没填或填了空值时，自动回退到这个值。
    ''' </summary>
    Public Const DefaultClientId As String = "cs-d8d61d31131cc5d0"

    ''' <summary>
    ''' 终端控制台。client_id 就是在这里自助登记的：
    ''' 输入访问密钥 → 切到「接入应用 / OAuth 2.0」→ 填应用名称和回调地址 →
    ''' 勾选「公开客户端」（桌面应用必须勾，不能保存密钥）→ 点「登记应用」。
    ''' </summary>
    Public Const ConsoleUrl As String = "https://caelus-terminal.app.workbuddy.host/"

    ''' <summary>官方接入文档（比飞书那份更全，含管理接口示例）</summary>
    Public Const DocsUrl As String = "https://caelus-terminal.app.workbuddy.host/docs/Caelus-OAuth.md"

    ''' <summary>
    ''' 申请的授权范围。与官方 SDK 示例保持一致：
    ''' star_id 默认授予（用户唯一编号）；profile 给昵称/创建时间；email 给邮箱。
    ''' </summary>
    Public Const Scope As String = "star_id profile email"

    Private Const TokenFileName As String = "starid-token.bin"
    Private Const ConfigFileName As String = "starid.txt"

    ' ---- 用户可配置项（存在 starid.txt，用户自己填）----

    Private _clientId As String = ""
    Private _redirectUri As String = ""
    Private _configLoaded As Boolean = False

    ''' <summary>客户端 ID，由管理员登记应用时生成</summary>
    Public ReadOnly Property ClientId As String
        Get
            EnsureConfig()
            Return _clientId
        End Get
    End Property

    ''' <summary>回调地址，必须与登记时填的完全一致</summary>
    Public ReadOnly Property RedirectUri As String
        Get
            EnsureConfig()
            Return _redirectUri
        End Get
    End Property

    ''' <summary>从回调地址里解析出监听端口</summary>
    Public ReadOnly Property RedirectPort As Integer
        Get
            Dim parsed As Uri = Nothing
            If Uri.TryCreate(RedirectUri, UriKind.Absolute, parsed) AndAlso parsed.Port > 0 Then
                Return parsed.Port
            End If
            Return 57321
        End Get
    End Property

    ''' <summary>是否已经填过客户端 ID</summary>
    Public ReadOnly Property IsConfigured As Boolean
        Get
            Return ClientId.Trim().Length > 0
        End Get
    End Property

    ''' <summary>保存配置（用户从「Star ID 配置」对话框填的）</summary>
    Public Sub SaveConfig(clientId As String, redirectUri As String)
        _clientId = If(clientId Is Nothing, "", clientId.Trim())
        _redirectUri = If(String.IsNullOrWhiteSpace(redirectUri), DefaultRedirectUri, redirectUri.Trim())
        _configLoaded = True

        Try
            Dim sb As New StringBuilder()
            sb.AppendLine("# Caelus Star ID 接入配置")
            sb.AppendLine("# 客户端 ID 由 Caelus Studio 管理员登记应用时生成")
            sb.AppendLine("# 回调地址必须与登记时填写的内容完全一致")
            sb.AppendLine("client_id=" & _clientId)
            sb.AppendLine("redirect_uri=" & _redirectUri)
            System.IO.File.WriteAllText(ConfigPath(), sb.ToString(), Encoding.UTF8)
        Catch
        End Try
    End Sub

    Private Sub EnsureConfig()
        If _configLoaded Then Return
        _configLoaded = True
        _clientId = DefaultClientId
        _redirectUri = DefaultRedirectUri

        Try
            Dim cfg = ConfigPath()
            If Not System.IO.File.Exists(cfg) Then Return

            For Each line In System.IO.File.ReadAllLines(cfg)
                Dim t = line.Trim()
                If t.Length = 0 OrElse t.StartsWith("#") Then Continue For
                Dim eq = t.IndexOf("="c)
                If eq <= 0 Then Continue For

                Dim k = t.Substring(0, eq).Trim().ToLowerInvariant()
                Dim v = t.Substring(eq + 1).Trim()
                If k = "client_id" Then _clientId = v
                If k = "redirect_uri" Then _redirectUri = v
            Next
        Catch
        End Try

        If _clientId.Length = 0 Then _clientId = DefaultClientId
        If _redirectUri.Length = 0 Then _redirectUri = DefaultRedirectUri
    End Sub

    Private Function ConfigPath() As String
        Return Path.Combine(DataDir(), ConfigFileName)
    End Function

    ' ==================== 状态 ====================

    Private _user As StarIDUser
    Private _accessToken As String = ""
    Private _refreshToken As String = ""
    Private _expiresAt As DateTime
    ''' <summary>服务端实际授予的授权范围（token 响应里的 scope），可能与申请的不完全一致</summary>
    Private _tokenScope As String = ""

    ''' <summary>服务端实际授予的授权范围；没拿到就退回申请时填的 Scope</summary>
    Public ReadOnly Property TokenScope As String
        Get
            Return If(_tokenScope.Length > 0, _tokenScope, Scope)
        End Get
    End Property

    ''' <summary>当前 access_token 的过期时间</summary>
    Public ReadOnly Property TokenExpiresAt As DateTime
        Get
            Return _expiresAt
        End Get
    End Property

    ''' <summary>当前已登录用户；未登录为 Nothing</summary>
    Public ReadOnly Property CurrentUser As StarIDUser
        Get
            Return _user
        End Get
    End Property

    Public Function IsLoggedIn() As Boolean
        Return _user IsNot Nothing AndAlso _user.StarId.Length > 0
    End Function

    Private Sub EnsureTls()
        Try
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
            ' .NET Framework 的 HttpWebRequest 默认会先发 Expect: 100-continue 再发 body，
            ' 有些服务端/反代对此处理不好会让连接被中断。关掉更稳。
            ServicePointManager.Expect100Continue = False
        Catch
        End Try
    End Sub

    ' ==================== 登录 ====================

    ''' <summary>
    ''' 打开浏览器让用户登录。返回用户信息；用户拒绝或超时返回 Nothing。
    ''' 异常会向外抛（调用方需要 Catch 提示）。
    ''' </summary>
    Public Async Function LoginAsync() As Task(Of StarIDUser)
        EnsureTls()

        Dim verifier = NewVerifier()
        Dim challenge = ChallengeOf(verifier)
        Dim state = NewState()

        Dim listener As New TcpListener(IPAddress.Loopback, RedirectPort)
        listener.Start()

        Try
            Process.Start(New ProcessStartInfo(BuildAuthorizeUrl(challenge, state)) With {
                .UseShellExecute = True
            })

            Dim query = Await WaitForCallbackAsync(listener)
            If query Is Nothing Then Return Nothing          ' 超时 / 用户没完成

            If query.ContainsKey("error") Then
                Dim desc = If(query.ContainsKey("error_description"), query("error_description"), query("error"))
                Throw New Exception("授权失败：" & desc)
            End If
            If Not query.ContainsKey("code") Then
                Throw New Exception("回调里没有授权码（code）。")
            End If
            If Not query.ContainsKey("state") OrElse query("state") <> state Then
                Throw New Exception("state 校验失败，已中止（可能是 CSRF 攻击）。")
            End If

            Await ExchangeCodeAsync(query("code"), verifier)
            _user = Await FetchUserAsync()

            If _user IsNot Nothing AndAlso _user.StarId.Length = 0 AndAlso _accessToken.Length > 0 Then
                ' 极端情况：userinfo 没给 star_id，用 token 响应里的兜底
                _user = New StarIDUser() With {.StarId = _starIdFromToken}
            End If

            SaveTokens()
            Return _user
        Finally
            Try
                listener.Stop()
            Catch
            End Try
        End Try
    End Function

    ''' <summary>退出登录：本地清除 + 服务端撤销</summary>
    Public Async Function LogoutAsync() As Task
        Dim token = _accessToken
        Dim refresh = _refreshToken

        _user = Nothing
        _accessToken = ""
        _refreshToken = ""
        _expiresAt = DateTime.MinValue
        _starIdFromToken = ""
        ClearTokens()

        If token.Length > 0 Then
            Try
                Await RevokeAsync(token)
            Catch
            End Try
        End If
        If refresh.Length > 0 Then
            Try
                Await RevokeAsync(refresh)
            Catch
            End Try
        End If
    End Function

    ''' <summary>
    ''' 启动时恢复会话：读本地令牌，过期就自动续期，然后取用户资料。
    ''' </summary>
    Public Async Function TryRestoreAsync() As Task(Of StarIDUser)
        If Not LoadTokens() Then Return Nothing

        Try
            If DateTime.Now >= _expiresAt.AddSeconds(-60) Then
                Dim ok = Await RefreshAsync()
                If Not ok Then
                    ClearTokens()
                    Return Nothing
                End If
            End If

            _user = Await FetchUserAsync()
            Return _user
        Catch
            Return Nothing
        End Try
    End Function

    ' ==================== OAuth 交互 ====================

    ''' <summary>
    ''' 构造授权页地址。参数与官方 SDK 的 login() 一致。
    ''' 额外带 prompt=consent：否则用户已授权过时，授权页会直接静默跳回，
    ''' 看不到任何反馈；强制显示同意页，体验更可控（官方 SDK 也支持这个参数）。
    ''' </summary>
    Private Function BuildAuthorizeUrl(challenge As String, state As String) As String
        Return BaseUrl & "/oauth/authorize" &
            "?client_id=" & Uri.EscapeDataString(ClientId) &
            "&redirect_uri=" & Uri.EscapeDataString(RedirectUri) &
            "&response_type=code" &
            "&scope=" & Uri.EscapeDataString(Scope) &
            "&state=" & Uri.EscapeDataString(state) &
            "&code_challenge=" & Uri.EscapeDataString(challenge) &
            "&code_challenge_method=S256" &
            "&prompt=consent"
    End Function

    ''' <summary>用授权码换令牌（公开客户端：只带 client_id + code_verifier）</summary>
    Private Async Function ExchangeCodeAsync(code As String, verifier As String) As Task
        Dim body = "{""grant_type"":""authorization_code"",""code"":""" & JsonEscape(code) &
                   """,""client_id"":""" & JsonEscape(ClientId) &
                   """,""code_verifier"":""" & JsonEscape(verifier) &
                   """,""redirect_uri"":""" & JsonEscape(RedirectUri) & """}"

        Dim json = Await PostAsync(BaseUrl & "/oauth/token", body)
        ReadTokenResponse(json)
    End Function

    Private _starIdFromToken As String = ""

    ''' <summary>refresh_token 续期（每次续期服务端都会轮换 refresh_token）</summary>
    Public Async Function RefreshAsync() As Task(Of Boolean)
        If _refreshToken.Length = 0 Then Return False

        Dim body = "{""grant_type"":""refresh_token"",""refresh_token"":""" & JsonEscape(_refreshToken) &
                   """,""client_id"":""" & JsonEscape(ClientId) & """}"

        Try
            Dim json = Await PostAsync(BaseUrl & "/oauth/token", body)
            ReadTokenResponse(json)
            SaveTokens()
            Return _accessToken.Length > 0
        Catch
            Return False
        End Try
    End Function

    Private Sub ReadTokenResponse(json As String)
        If json Is Nothing OrElse json.Length = 0 Then
            Throw New Exception("令牌接口无响应。")
        End If

        Dim err = JsonString(json, "error")
        If err.Length > 0 Then
            Dim desc = JsonString(json, "error_description")
            Throw New Exception("换取令牌失败：" & err & If(desc.Length > 0, "（" & desc & "）", ""))
        End If

        _accessToken = JsonString(json, "access_token")
        _refreshToken = JsonString(json, "refresh_token")
        _starIdFromToken = JsonString(json, "star_id")

        ' 服务端实际授予的范围（示例返回的是 "email profile star_id" 这种空格分隔串）
        Dim sc = JsonString(json, "scope")
        If sc.Length > 0 Then _tokenScope = sc

        Dim secs As Double = 3600
        Dim raw = JsonString(json, "expires_in")
        If raw.Length > 0 Then Double.TryParse(raw, secs)
        If secs <= 0 Then secs = 3600
        _expiresAt = DateTime.Now.AddSeconds(secs)

        If _accessToken.Length = 0 Then
            Throw New Exception("令牌响应里没有 access_token。")
        End If
    End Sub

    ''' <summary>取用户资料；令牌过期会自动续期一次</summary>
    Public Async Function FetchUserAsync() As Task(Of StarIDUser)
        If _accessToken.Length = 0 Then Return Nothing

        ' 注意：VB 不允许在 Catch 块里 Await，所以先记下失败，出了 Try 再续期
        Dim json As String = Nothing
        Dim failed = False
        Try
            json = Await PostUserInfoAsync()
        Catch
            failed = True
        End Try

        If failed Then
            If Not (Await RefreshAsync()) Then
                Throw New Exception("登录已过期，且续期失败。")
            End If
            json = Await PostUserInfoAsync()
        End If

        Dim u As New StarIDUser()
        u.StarId = JsonString(json, "star_id")
        If u.StarId.Length = 0 Then u.StarId = JsonString(json, "sub")
        u.Nickname = JsonString(json, "nickname")
        u.Email = JsonString(json, "email")
        u.EmailVerified = (JsonString(json, "email_verified") = "true")

        Dim created As Long = 0
        Long.TryParse(JsonString(json, "created_at"), created)
        u.CreatedAt = created

        Return u
    End Function

    Private Async Function RevokeAsync(token As String) As Task
        Dim body = "{""token"":""" & JsonEscape(token) & """}"
        Await PostAsync(BaseUrl & "/oauth/revoke", body)
    End Function

    ' ==================== 本地回环回调 ====================

    ''' <summary>
    ''' 等待浏览器带着 code 回调。用 TcpListener 而不是 HttpListener，
    ''' 因为后者在普通用户权限下绑定 http://127.0.0.1:port 可能需要 netsh 预留。
    ''' 5 分钟没等到就放弃。
    '''
    ''' 这里不能「接一个连接就完事」：浏览器（尤其 Chrome / Edge）会提前建「预连接」——
    ''' 连上来却不发请求，或者发完立刻关掉。老写法接到这种连接后仍然去写响应，
    ''' 就会抛「无法将数据写入传输连接」（因为对端已经关了）。
    ''' 所以：拿不到 code 就继续接下一个，写响应失败也绝不能算登录失败。
    ''' </summary>
    Private Async Function WaitForCallbackAsync(listener As TcpListener) As Task(Of Dictionary(Of String, String))
        Dim deadline = DateTime.UtcNow.AddMinutes(5)

        While True
            Dim remain = deadline - DateTime.UtcNow
            If remain <= TimeSpan.Zero Then Return Nothing

            Dim acceptTask = listener.AcceptTcpClientAsync()
            If Await Task.WhenAny(acceptTask, Task.Delay(remain)) IsNot acceptTask Then Return Nothing

            Dim client As TcpClient
            Try
                client = Await acceptTask
            Catch
                Return Nothing
            End Try

            Dim got = Await ReadCallbackAsync(client)
            If got IsNot Nothing Then Return got
            ' 不是回调（预连接 / favicon 之类）：丢掉这个连接，继续等下一个
        End While

        ' While True 只靠 Return 退出，编译器分析不出来，不补这句会报 BC42105
        Return Nothing
    End Function

    ''' <summary>
    ''' 处理单个连接：读出请求头 → 解析 query → 尽量回一个网页。
    ''' 对端提前断开、读超时、格式不对……统统返回 Nothing 让外层继续等，
    ''' 绝不能把异常抛给调用方（否则一次预连接就能把整个登录打断）。
    ''' </summary>
    Private Async Function ReadCallbackAsync(client As TcpClient) As Task(Of Dictionary(Of String, String))
        Try
            Using client
                Using ns = client.GetStream()
                    Dim request = Await ReadRequestHeadAsync(ns)
                    If request Is Nothing Then Return Nothing

                    ' 先解析再写响应：就算写失败，授权码也已经到手了
                    Dim query = ExtractQuery(request)
                    Await TryReplyAsync(ns)
                    Return query
                End Using
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 读完 HTTP 请求头。对端提前关闭（预连接就是这样）或等超时都返回 Nothing。
    ''' 单连接只给 5 秒：正常回调里浏览器是立刻发请求的，空闲连接没必要一直挂着。
    ''' </summary>
    Private Async Function ReadRequestHeadAsync(ns As Stream) As Task(Of String)
        Dim sb As New StringBuilder()
        Dim buf(8191) As Byte

        Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(5))
            Try
                While True
                    Dim n = Await ns.ReadAsync(buf, 0, buf.Length, cts.Token)
                    If n <= 0 Then Return Nothing
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n))
                    If sb.ToString().IndexOf(vbCrLf & vbCrLf, StringComparison.Ordinal) >= 0 Then Exit While
                End While
            Catch
                Return Nothing
            End Try
        End Using

        Return sb.ToString()
    End Function

    ''' <summary>从请求行里取出 ? 后面的查询串；没有就返回 Nothing</summary>
    Private Function ExtractQuery(request As String) As Dictionary(Of String, String)
        Dim firstLine = request.Split(New String() {vbCrLf}, StringSplitOptions.None)(0)
        Dim parts = firstLine.Split(" "c)
        If parts.Length < 2 Then Return Nothing

        Dim q = parts(1).IndexOf("?"c)
        If q < 0 Then Return Nothing
        Return ParseQuery(parts(1).Substring(q + 1))
    End Function

    ''' <summary>
    ''' 回一个「登录完成」页面。用户可能已经把标签页关了，写失败很正常，
    ''' 这里必须静默吃掉 —— 它和登录成不成功没有关系。
    ''' </summary>
    Private Async Function TryReplyAsync(ns As Stream) As Task
        Try
            Dim html = "<html><head><meta charset='utf-8'><title>Caelus Star ID</title></head>" &
                       "<body style='font-family:sans-serif;text-align:center;padding-top:80px'>" &
                       "<h2>登录完成</h2><p>可以关闭这个页面，回到 Caelus Studio 了。</p></body></html>"
            Dim bodyBytes = Encoding.UTF8.GetBytes(html)
            Dim head = "HTTP/1.1 200 OK" & vbCrLf &
                       "Content-Type: text/html; charset=utf-8" & vbCrLf &
                       "Content-Length: " & bodyBytes.Length.ToString() & vbCrLf &
                       "Connection: close" & vbCrLf & vbCrLf
            Dim headBytes = Encoding.ASCII.GetBytes(head)

            Await ns.WriteAsync(headBytes, 0, headBytes.Length)
            Await ns.WriteAsync(bodyBytes, 0, bodyBytes.Length)
            Await ns.FlushAsync()
        Catch
        End Try
    End Function

    Private Function ParseQuery(query As String) As Dictionary(Of String, String)
        Dim dict As New Dictionary(Of String, String)()
        For Each pair In query.Split("&"c)
            If pair.Length = 0 Then Continue For
            Dim eq = pair.IndexOf("="c)
            If eq < 0 Then
                dict(Uri.UnescapeDataString(pair)) = ""
            Else
                dict(Uri.UnescapeDataString(pair.Substring(0, eq))) =
                    Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+"c, " "c))
            End If
        Next
        Return dict
    End Function

    ' ==================== PKCE ====================

    Private Function NewVerifier() As String
        Dim b(31) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(b)
        End Using
        Return Base64Url(b)
    End Function

    Private Function ChallengeOf(verifier As String) As String
        Using sha = SHA256.Create()
            Return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)))
        End Using
    End Function

    Private Function NewState() As String
        Dim b(15) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(b)
        End Using
        Return Base64Url(b)
    End Function

    Private Function Base64Url(data As Byte()) As String
        Return Convert.ToBase64String(data).Replace("+"c, "-"c).Replace("/"c, "_"c).TrimEnd("="c)
    End Function

    ' ==================== HTTP ====================

    Private Async Function PostAsync(url As String, jsonBody As String) As Task(Of String)
        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(30)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")

            Dim content As New StringContent(jsonBody, Encoding.UTF8, "application/json")
            Dim response = Await client.PostAsync(url, content)
            Dim text = Await response.Content.ReadAsStringAsync()

            If Not response.IsSuccessStatusCode Then
                Dim desc = JsonString(text, "error_description")
                If desc.Length = 0 Then desc = JsonString(text, "error")
                If desc.Length = 0 Then desc = text
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString() & "：" & desc)
            End If
            Return text
        End Using
    End Function

    Private Async Function GetAsync(url As String, bearer As String) As Task(Of String)
        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(30)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")
            client.DefaultRequestHeaders.Authorization =
                New Headers.AuthenticationHeaderValue("Bearer", bearer)

            Dim response = Await client.GetAsync(url)
            Dim text = Await response.Content.ReadAsStringAsync()

            If Not response.IsSuccessStatusCode Then
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString())
            End If
            Return text
        End Using
    End Function

    ''' <summary>
    ''' 取 userinfo 的原始 JSON。
    ''' ⚠️ 本部署的网关会剥离 Authorization 头（官方 SDK /sdk/caelus-auth.js 注释里明确写了），
    ''' 老写法「GET + Bearer 头」会被剥掉令牌 → 服务端收不到 → 恒 401（已踩）。
    ''' 正解与官方 SDK 完全一致：POST /oauth/userinfo，access_token 放进 JSON 请求体；
    ''' Authorization 头照样带上，两处都给、以请求体为准。
    ''' </summary>
    Private Async Function PostUserInfoAsync() As Task(Of String)
        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(30)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")
            client.DefaultRequestHeaders.Authorization =
                New Headers.AuthenticationHeaderValue("Bearer", _accessToken)

            Dim body = "{""access_token"":""" & JsonEscape(_accessToken) & """}"
            Dim content As New StringContent(body, Encoding.UTF8, "application/json")
            Dim response = Await client.PostAsync(BaseUrl & "/oauth/userinfo", content)
            Dim text = Await response.Content.ReadAsStringAsync()

            If Not response.IsSuccessStatusCode Then
                Dim desc = JsonString(text, "error_description")
                If desc.Length = 0 Then desc = JsonString(text, "error")
                If desc.Length = 0 Then desc = text
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString() & "：" & desc)
            End If
            Return text
        End Using
    End Function

    ' ==================== 令牌存储（DPAPI 加密） ====================

    ''' <summary>本应用的本地数据目录（配置和令牌都放这）</summary>
    Private Function DataDir() As String
        Dim dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caelus Studio")
        Try
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
        Catch
        End Try
        Return dir
    End Function

    Private Function TokenPath() As String
        Return Path.Combine(DataDir(), TokenFileName)
    End Function

    Private Sub SaveTokens()
        Try
            Dim sb As New StringBuilder()
            sb.AppendLine(_accessToken)
            sb.AppendLine(_refreshToken)
            sb.AppendLine(_expiresAt.ToBinary().ToString())
            sb.AppendLine(_starIdFromToken)
            sb.AppendLine(If(_user Is Nothing, "", _user.Nickname))
            ' 第 6 行是后来加的授权范围；旧令牌文件没有这一行，读取时按缺失处理
            sb.AppendLine(_tokenScope)

            Dim plain = Encoding.UTF8.GetBytes(sb.ToString())
            Dim enc = ProtectedData.Protect(plain, Nothing, DataProtectionScope.CurrentUser)
            File.WriteAllBytes(TokenPath(), enc)
        Catch
        End Try
    End Sub

    Private Function LoadTokens() As Boolean
        Try
            Dim cfg = TokenPath()
            If Not File.Exists(cfg) Then Return False

            Dim enc = File.ReadAllBytes(cfg)
            Dim plain As Byte()
            Try
                plain = ProtectedData.Unprotect(enc, Nothing, DataProtectionScope.CurrentUser)
            Catch
                plain = enc          ' 兼容未加密的旧文件
            End Try

            Dim lines = Encoding.UTF8.GetString(plain).Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
            If lines.Length < 4 Then Return False

            _accessToken = lines(0)
            _refreshToken = lines(1)
            Dim bin As Long = 0
            Long.TryParse(lines(2), bin)
            Try
                _expiresAt = DateTime.FromBinary(bin)
            Catch
                _expiresAt = DateTime.MinValue
            End Try
            _starIdFromToken = lines(3)

            ' 授权范围是后加的第 6 行，旧文件没有就留空（TokenScope 会自动退回申请的 Scope）
            _tokenScope = If(lines.Length >= 6, lines(5), "")

            If _accessToken.Length = 0 Then Return False
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Sub ClearTokens()
        Try
            Dim cfg = TokenPath()
            If File.Exists(cfg) Then File.Delete(cfg)
        Catch
        End Try
    End Sub

    ' ==================== 极简 JSON 读取 ====================
    ' 这几个接口的响应都是扁平结构，没必要为它引第三方库。

    Private Function JsonString(json As String, key As String) As String
        If json Is Nothing Then Return ""
        Dim i = json.IndexOf("""" & key & """", StringComparison.Ordinal)
        If i < 0 Then Return ""

        Dim c = json.IndexOf(":"c, i)
        If c < 0 Then Return ""
        c += 1
        While c < json.Length AndAlso Char.IsWhiteSpace(json(c))
            c += 1
        End While
        If c >= json.Length Then Return ""

        If json(c) = """"c Then
            Dim e = c + 1
            Dim sb As New StringBuilder()
            While e < json.Length
                Dim ch = json(e)
                If ch = "\"c Then
                    e += 1
                    If e < json.Length Then
                        Select Case json(e)
                            Case "n"c : sb.Append(ChrW(10))
                            Case "t"c : sb.Append(ChrW(9))
                            Case "r"c : sb.Append(ChrW(13))
                            Case "u"c
                                If e + 4 < json.Length Then
                                    sb.Append(ChrW(Convert.ToInt32(json.Substring(e + 1, 4), 16)))
                                End If
                                e += 4
                            Case Else : sb.Append(json(e))
                        End Select
                    End If
                ElseIf ch = """"c Then
                    Exit While
                Else
                    sb.Append(ch)
                End If
                e += 1
            End While
            Return sb.ToString()
        Else
            Dim e = c
            While e < json.Length AndAlso json(e) <> ","c AndAlso json(e) <> "}"c AndAlso
                  json(e) <> "]"c AndAlso Not Char.IsWhiteSpace(json(e))
                e += 1
            End While
            Return json.Substring(c, e - c).Trim(""""c)
        End If
    End Function

    Private Function JsonEscape(text As String) As String
        If text Is Nothing Then Return ""
        Dim sb As New StringBuilder()
        For Each ch In text
            Select Case ch
                Case "\"c : sb.Append("\\")
                Case """"c : sb.Append("\""")
                Case ChrW(10) : sb.Append("\n")
                Case ChrW(13) : sb.Append("\r")
                Case ChrW(9) : sb.Append("\t")
                Case Else : sb.Append(ch)
            End Select
        Next
        Return sb.ToString()
    End Function

End Module
