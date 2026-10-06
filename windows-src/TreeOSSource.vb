Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Collections.Generic
Imports System.Text
Imports System.Windows.Media.Imaging

''' <summary>
''' TreeOS 发布信息，来自 GitHub 仓库 caelustudio/TreeOS 根目录下的 download.txt
''' </summary>
Public Class TreeOSReleaseInfo
    Public Property Url As String = ""
    Public Property Name As String = ""
    Public Property Version As String = ""
    Public Property Size As String = ""
    Public Property Logo As String = ""
End Class

''' <summary>
''' 单次下载进度汇报
''' </summary>
Public Class DownloadReport
    Public Property Received As Long = 0
    Public Property Total As Long = 0
    Public Property Speed As Double = 0
End Class

''' <summary>
''' 云端管理台下发的运行配置：两个作品的下载开关 + 应用自身的更新信息。
''' 数据存在 WorkBuddy 云数据库，管理页在 https://caelus-admin-49685.app.workbuddy.host/
''' 读不到配置时全部走默认值，不影响离线使用。
''' </summary>
Public Class AppConfig
    Public Property TreeOsEnabled As Boolean = True
    Public Property TreeOsVersion As String = ""
    Public Property TreeOsUrl As String = ""
    Public Property TreeOsNote As String = ""
    Public Property CaelusOsEnabled As Boolean = True
    Public Property CaelusOsVersion As String = ""
    Public Property CaelusOsUrl As String = ""
    Public Property CaelusOsNote As String = ""
    Public Property AppVersion As String = ""
    Public Property AppUrl As String = ""
    Public Property AppNotes As String = ""
    Public Property AppForce As Boolean = False

    ' ---- 版本回退 ----
    ' 新版本出问题时，在管理台开一次「强制回退」，把高于目标版本的客户端全部拉回去。
    ' 回退优先级高于更新：需要回退时不再提示更新，避免刚回退完又被推上去。
    Public Property AppRollback As Boolean = False
    ''' <summary>回退目标版本号（例如 1.1.0）</summary>
    Public Property AppRollbackTo As String = ""
    ''' <summary>回退包地址，留空则用 app_url</summary>
    Public Property AppRollbackUrl As String = ""
    ''' <summary>回退说明，展示给用户</summary>
    Public Property AppRollbackNotes As String = ""

    ''' <summary>True 表示这一份确实来自云端；False 表示读取失败、用的全是默认值</summary>
    Public Property Loaded As Boolean = False
End Class

''' <summary>
''' 商店里的一件商品。所有字段都由云端管理台下发，客户端不内置任何商品。
''' </summary>
Public Class ShopItem
    Public Property Id As Integer = 0
    Public Property Name As String = ""
    ''' <summary>角标文案，例如「周边」「新品」，留空则不显示</summary>
    Public Property Tag As String = ""
    Public Property Description As String = ""
    ''' <summary>价格，单位元；0 表示免费 / 待定（界面显示「免费」）</summary>
    Public Property Price As Decimal = 0D
    Public Property ImageUrl As String = ""
    ''' <summary>「立即购买」跳转的链接，留空则隐藏购买按钮</summary>
    Public Property BuyUrl As String = ""
    ''' <summary>数字内容的下载地址，只有 DownloadEnabled 为真时卡片才给下载按钮</summary>
    Public Property DownloadUrl As String = ""
    Public Property DownloadEnabled As Boolean = False
    Public Property Visible As Boolean = True
    Public Property SortOrder As Integer = 0

    ''' <summary>格式化后的价格文案：0 显示「免费」，其余显示 ￥9.9</summary>
    Public ReadOnly Property PriceText As String
        Get
            If Price <= 0D Then Return "免费"
            Return "￥" & Price.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
        End Get
    End Property
End Class

Public Module TreeOSSource

    ' 下载地址配置（主源 + jsDelivr 镜像，主源失败自动回退）
    Public Const ConfigUrl As String = "https://raw.githubusercontent.com/caelustudio/TreeOS/main/download.txt"
    Public Const ConfigMirrorUrl As String = "https://cdn.jsdelivr.net/gh/caelustudio/TreeOS@main/download.txt"
    ' download.caelus.top 指向 caelustudio/APP 仓库（该仓库也有一份 download.txt）
    Public Const ConfigPagesUrl As String = "https://download.caelus.top/download.txt"
    ' 导航栏「GitHub」按钮的跳转地址 —— 指向 Caelus Studio 组织主页
    Public Const RepositoryUrl As String = "https://github.com/caelustudio/"

    ' ---- 云端管理台（WorkBuddy 云数据库，PostgREST 数据平面）----
    ' 管理页读写 app_config 表；桌面端只做匿名只读。
    ' 注意：这里不是浏览器，没有 Origin 头，服务端对这种原生调用也放行（实测 HTTP 200）。
    Public Const ConfigApiUrl As String = "https://caelus-admin-49685.app.workbuddy.host/.cloud/database/rest/app_config?select=*"
    Public Const ConfigApiKey As String = "wbpk_3FeJBY8bCVkyXBb3HKrG8O_tv2mKiS6jmIprzpY0O9BWD28tAWlTQXF"

    ' 商店商品库。同样的规则：URL 上只出现 PostgREST 认识的 select / order，
    ' 别往后面追加任何自定义参数，否则会被当成「按列过滤」返回 400。
    Public Const ShopApiUrl As String = "https://caelus-admin-49685.app.workbuddy.host/.cloud/database/rest/shop_items?select=*&order=sort_order.asc,id.asc"

    ''' <summary>本应用的版本号，与云端 app_version 比对判断是否有更新</summary>
    Public Const LocalAppVersion As String = "1.2.1"

    ' Logo 候选地址（download.txt 里的 logo= 优先）
    Public Const DefaultLogoUrl As String = "https://raw.githubusercontent.com/caelustudio/TreeOS/main/logo.txt"
    Private Const LogoPngUrl As String = "https://raw.githubusercontent.com/caelustudio/TreeOS/main/logo.png"
    Private Const LogoMirrorUrl As String = "https://cdn.jsdelivr.net/gh/caelustudio/TreeOS@main/logo.txt"
    Private Const LogoPagesUrl As String = "https://download.caelus.top/logo.txt"

    ' 工作室图标（首页 / 标题栏用），来自官网固定地址。注意空格要转义成 %20
    Public Const StudioLogoUrl As String = "https://www.caelus.top/App/Caelus%20Studio-new.png"

    ' ---- 各作品的下载直链 ----
    ' 都放在 TreeOS 仓库里，download.caelus.top 是该仓库的 GitHub Pages 域名。

    ''' <summary>TreeOS 下载直链</summary>
    Public Const TreeOsUrl As String = "https://download.caelus.top/TreeOS/TreeOS.rar"

    ''' <summary>CaelusOS 图标</summary>
    Public Const CaelusOsLogoUrl As String = "https://download.caelus.top/Logo.png"

    ''' <summary>CaelusOS 下载直链</summary>
    Public Const CaelusOsUrl As String = "https://download.caelus.top/CaelusOS/CaelusOS.pptm"

    ''' <summary>
    ''' .NET Framework 4.8 默认可能不走 TLS 1.2，访问 GitHub 前必须先开启
    ''' </summary>
    Public Sub EnsureTls()
        Try
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
            ServicePointManager.Expect100Continue = False
            ServicePointManager.DefaultConnectionLimit = 16
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 读取 download.txt 并解析出下载地址
    ''' </summary>
    Public Async Function FetchReleaseInfoAsync() As Task(Of TreeOSReleaseInfo)
        EnsureTls()
        Dim lastError As String = ""

        ' 镜像优先：raw.githubusercontent 在国内经常连不通，放最后
        For Each base In New String() {ConfigMirrorUrl, ConfigPagesUrl, ConfigUrl}
            Try
                Dim text = Await GetTextAsync(base)
                If Not String.IsNullOrWhiteSpace(text) Then
                    Return Parse(text)
                End If
            Catch ex As Exception
                lastError = ex.Message
            End Try
        Next

        Dim detail As String = If(String.IsNullOrEmpty(lastError), "", "（" & lastError & "）")
        Throw New Exception("无法读取下载地址配置，请检查网络连接。" & detail)
    End Function

    ''' <summary>
    ''' 解析配置文本：支持 "url=xxx" 形式，也支持直接把地址写在第一行
    ''' </summary>
    Public Function Parse(text As String) As TreeOSReleaseInfo
        Dim info As New TreeOSReleaseInfo()
        If String.IsNullOrEmpty(text) Then Return info

        For Each rawLine In text.Split(New Char() {ChrW(10), ChrW(13)}, StringSplitOptions.RemoveEmptyEntries)
            Dim line = rawLine.Trim()
            If String.IsNullOrEmpty(line) Then Continue For
            If line.StartsWith("#") OrElse line.StartsWith(";") OrElse line.StartsWith("//") Then Continue For

            Dim eq = line.IndexOf("="c)
            If eq > 0 Then
                Dim key = line.Substring(0, eq).Trim().ToLowerInvariant()
                Dim value = line.Substring(eq + 1).Trim().Trim(""""c)
                Select Case key
                    Case "url", "link", "download", "address"
                        info.Url = value
                    Case "name", "file", "filename"
                        info.Name = value
                    Case "version", "ver"
                        info.Version = value
                    Case "size"
                        info.Size = value
                    Case "logo", "icon", "image"
                        info.Logo = value
                End Select
            ElseIf String.IsNullOrEmpty(info.Url) AndAlso
                   line.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                info.Url = line
            End If
        Next

        Return info
    End Function

    Private Async Function GetTextAsync(url As String) As Task(Of String)
        Dim sep As String = If(url.Contains("?"), "&", "?")
        Dim full = url & sep & "t=" & DateTime.UtcNow.Ticks.ToString()

        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(20)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache")
            Dim response = Await client.GetAsync(full)
            If Not response.IsSuccessStatusCode Then
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString())
            End If
            Return Await response.Content.ReadAsStringAsync()
        End Using
    End Function

    ''' <summary>
    ''' 带缓存的配置读取，多个页面共用一份，避免重复请求
    ''' </summary>
    Public Function GetReleaseInfoAsync(forceRefresh As Boolean) As Task(Of TreeOSReleaseInfo)
        If forceRefresh Then _infoTask = Nothing
        If _infoTask Is Nothing Then
            _infoTask = FetchReleaseInfoAsync()
        End If
        Return _infoTask
    End Function

    ' ==================== 云端管理台配置 ====================

    Private _configTask As Task(Of AppConfig)

    ''' <summary>
    ''' 读取云端管理台配置，带缓存。读不到时返回一份默认配置（Loaded=False），
    ''' 调用方按 Loaded 决定是否应用，保证断网时应用照常可用。
    ''' </summary>
    Public Function GetAppConfigAsync(forceRefresh As Boolean) As Task(Of AppConfig)
        If forceRefresh Then _configTask = Nothing
        If _configTask Is Nothing Then
            _configTask = FetchAppConfigAsync()
        End If
        Return _configTask
    End Function

    Public Async Function FetchAppConfigAsync() As Task(Of AppConfig)
        EnsureTls()
        Try
            Dim text = Await GetConfigTextAsync(ConfigApiUrl)
            Return ParseAppConfig(text)
        Catch
            ' 配置读取失败绝不能影响主流程，回退默认值
            Return New AppConfig()
        End Try
    End Function

    Private Async Function GetConfigTextAsync(url As String) As Task(Of String)
        ' ⚠️ 千万别往这个 URL 上追加 t=<时间戳> 之类的自定义参数。
        ' PostgREST 会把任何未知查询参数当作「按该列过滤」处理，t 不是列名就返回
        ' HTTP 400 "failed to parse filter"。防缓存只能用请求头，不能改 URL。
        ' （下载静态文件的 GetTextAsync / DownloadBytesAsync 照旧可以追加，那边无害）
        Using client As New HttpClient()
            ' 启动路径上调用，超时别设太长
            client.Timeout = TimeSpan.FromSeconds(10)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache")
            client.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache")
            ' 自定义头，用 Try 版本绕开头名校验
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-wb-webapp-access-key", ConfigApiKey)

            Dim response = Await client.GetAsync(url)
            If Not response.IsSuccessStatusCode Then
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString())
            End If
            Return Await response.Content.ReadAsStringAsync()
        End Using
    End Function

    ''' <summary>
    ''' 解析数据平面返回的 JSON（形如 [{...}]）。
    ''' 这里手写一个极简解析而不用 DataContractJsonSerializer，
    ''' 是为了不往老项目里加新的程序集引用。
    ''' </summary>
    Public Function ParseAppConfig(text As String) As AppConfig
        Dim cfg As New AppConfig()
        If String.IsNullOrWhiteSpace(text) Then Return cfg

        Dim start = text.IndexOf("{"c)
        Dim finish = text.LastIndexOf("}"c)
        If start < 0 OrElse finish <= start Then Return cfg

        Dim body = text.Substring(start + 1, finish - start - 1)
        Dim map = JsonValues(body)
        If map.Count = 0 Then Return cfg

        cfg.TreeOsEnabled = JsonBool(map, "treeos_enabled", True)
        cfg.TreeOsVersion = JsonStr(map, "treeos_version")
        cfg.TreeOsUrl = JsonStr(map, "treeos_url")
        cfg.TreeOsNote = JsonStr(map, "treeos_note")

        cfg.CaelusOsEnabled = JsonBool(map, "caelusos_enabled", True)
        cfg.CaelusOsVersion = JsonStr(map, "caelusos_version")
        cfg.CaelusOsUrl = JsonStr(map, "caelusos_url")
        cfg.CaelusOsNote = JsonStr(map, "caelusos_note")

        cfg.AppVersion = JsonStr(map, "app_version")
        cfg.AppUrl = JsonStr(map, "app_url")
        cfg.AppNotes = JsonStr(map, "app_notes")
        cfg.AppForce = JsonBool(map, "app_force", False)

        cfg.AppRollback = JsonBool(map, "app_rollback", False)
        cfg.AppRollbackTo = JsonStr(map, "app_rollback_to")
        cfg.AppRollbackUrl = JsonStr(map, "app_rollback_url")
        cfg.AppRollbackNotes = JsonStr(map, "app_rollback_notes")

        cfg.Loaded = True
        Return cfg
    End Function

    ''' <summary>把 JSON 对象体解析成 键-值 字典（值已去掉引号并反转义）</summary>
    Private Function JsonValues(body As String) As Dictionary(Of String, String)
        Dim map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim i As Integer = 0

        While i < body.Length
            Dim q1 = body.IndexOf(""""c, i)
            If q1 < 0 Then Exit While
            Dim q2 = JsonStringEnd(body, q1)
            If q2 < 0 Then Exit While

            Dim key = body.Substring(q1 + 1, q2 - q1 - 1)
            Dim colon = body.IndexOf(":"c, q2 + 1)
            If colon < 0 Then Exit While

            Dim j = colon + 1
            While j < body.Length AndAlso Char.IsWhiteSpace(body(j))
                j += 1
            End While
            If j >= body.Length Then Exit While

            Dim raw As String
            Dim nextPos As Integer

            If body(j) = """"c Then
                Dim e = JsonStringEnd(body, j)
                If e < 0 Then Exit While
                raw = JsonUnescape(body.Substring(j + 1, e - j - 1))
                nextPos = e + 1
            Else
                Dim k = j
                While k < body.Length AndAlso body(k) <> ","c
                    k += 1
                End While
                raw = body.Substring(j, k - j).Trim()
                nextPos = k
            End If

            map(key) = raw
            i = nextPos + 1
        End While

        Return map
    End Function

    ''' <summary>从起始引号找到配对结束引号（跳过 \" ）</summary>
    Private Function JsonStringEnd(text As String, startQuote As Integer) As Integer
        Dim i = startQuote + 1
        While i < text.Length
            If text(i) = "\"c Then
                i += 2
                Continue While
            End If
            If text(i) = """"c Then Return i
            i += 1
        End While
        Return -1
    End Function

    Private Function JsonUnescape(text As String) As String
        If text.IndexOf("\"c) < 0 Then Return text

        Dim sb As New StringBuilder()
        Dim i = 0
        While i < text.Length
            If text(i) = "\"c AndAlso i + 1 < text.Length Then
                Dim c = text(i + 1)
                Select Case c
                    Case "n"c
                        sb.Append(ChrW(10))
                        i += 2
                    Case "r"c
                        sb.Append(ChrW(13))
                        i += 2
                    Case "t"c
                        sb.Append(ChrW(9))
                        i += 2
                    Case "b"c
                        sb.Append(ChrW(8))
                        i += 2
                    Case "f"c
                        sb.Append(ChrW(12))
                        i += 2
                    Case "u"c
                        If i + 5 < text.Length Then
                            Dim code As Integer
                            If Integer.TryParse(text.Substring(i + 2, 4),
                                                System.Globalization.NumberStyles.HexNumber,
                                                System.Globalization.CultureInfo.InvariantCulture, code) Then
                                sb.Append(ChrW(code))
                                i += 6
                            Else
                                i += 2
                            End If
                        Else
                            i += 2
                        End If
                    Case Else
                        sb.Append(c)
                        i += 2
                End Select
            Else
                sb.Append(text(i))
                i += 1
            End If
        End While
        Return sb.ToString()
    End Function

    Private Function JsonStr(map As Dictionary(Of String, String), key As String) As String
        Dim value As String = Nothing
        If Not map.TryGetValue(key, value) Then Return ""
        If value Is Nothing Then Return ""
        If String.Equals(value, "null", StringComparison.OrdinalIgnoreCase) Then Return ""
        Return value
    End Function

    Private Function JsonBool(map As Dictionary(Of String, String), key As String,
                              defaultValue As Boolean) As Boolean
        Dim value As String = Nothing
        If Not map.TryGetValue(key, value) Then Return defaultValue
        If String.IsNullOrWhiteSpace(value) Then Return defaultValue
        If String.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase) Then Return True
        If String.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase) Then Return False
        If String.Equals(value.Trim(), "1", StringComparison.Ordinal) Then Return True
        If String.Equals(value.Trim(), "0", StringComparison.Ordinal) Then Return False
        Return defaultValue
    End Function

    ' ==================== 商店 ====================

    Private _shopTask As Task(Of List(Of ShopItem))

    ''' <summary>
    ''' 读取商店商品。服务端按 sort_order / id 排好序；
    ''' 匿名角色只能读到 visible=true 的行（RLS 挡住，下架商品根本不会下发）。
    ''' </summary>
    Public Function GetShopItemsAsync(forceRefresh As Boolean) As Task(Of List(Of ShopItem))
        If forceRefresh OrElse _shopTask Is Nothing Then
            _shopTask = LoadShopItemsAsync()
        End If
        Return _shopTask
    End Function

    Private Async Function LoadShopItemsAsync() As Task(Of List(Of ShopItem))
        Try
            Dim text = Await GetConfigTextAsync(ShopApiUrl)
            Return ParseShopItems(text)
        Catch
            Return New List(Of ShopItem)()
        End Try
    End Function

    ''' <summary>解析数据平面返回的商品数组（形如 [{...},{...}]）</summary>
    Public Function ParseShopItems(text As String) As List(Of ShopItem)
        Dim list As New List(Of ShopItem)()
        If String.IsNullOrWhiteSpace(text) Then Return list

        For Each body In JsonObjectBodies(text)
            Dim map = JsonValues(body)
            If map.Count = 0 Then Continue For

            Dim item As New ShopItem()
            item.Id = JsonInt(map, "id", 0)
            item.Name = JsonStr(map, "name")
            item.Tag = JsonStr(map, "tag")
            item.Description = JsonStr(map, "description")
            item.Price = JsonDec(map, "price", 0D)
            item.ImageUrl = JsonStr(map, "image_url")
            item.BuyUrl = JsonStr(map, "buy_url")
            item.DownloadUrl = JsonStr(map, "download_url")
            item.DownloadEnabled = JsonBool(map, "download_enabled", False)
            item.Visible = JsonBool(map, "visible", True)
            item.SortOrder = JsonInt(map, "sort_order", 0)

            If item.Name.Trim().Length > 0 Then list.Add(item)
        Next

        Return list
    End Function

    ''' <summary>
    ''' 把一个 JSON 数组里的每个顶层对象体切出来。
    ''' 必须跟踪字符串状态 —— 商品描述里完全可能出现 { } 这种字符。
    ''' </summary>
    Private Function JsonObjectBodies(text As String) As List(Of String)
        Dim bodies As New List(Of String)()
        Dim depth As Integer = 0
        Dim startPos As Integer = -1
        Dim inString As Boolean = False
        Dim i As Integer = 0

        While i < text.Length
            Dim c = text(i)
            If inString Then
                If c = "\"c Then
                    i += 1
                ElseIf c = """"c Then
                    inString = False
                End If
            Else
                If c = """"c Then
                    inString = True
                ElseIf c = "{"c Then
                    If depth = 0 Then startPos = i
                    depth += 1
                ElseIf c = "}"c Then
                    If depth > 0 Then
                        depth -= 1
                        If depth = 0 AndAlso startPos >= 0 Then
                            bodies.Add(text.Substring(startPos + 1, i - startPos - 1))
                            startPos = -1
                        End If
                    End If
                End If
            End If
            i += 1
        End While

        Return bodies
    End Function

    Private Function JsonInt(map As Dictionary(Of String, String), key As String,
                             defaultValue As Integer) As Integer
        Dim value = JsonStr(map, key)
        Dim n As Integer = 0
        If Integer.TryParse(value, Globalization.NumberStyles.Integer,
                            Globalization.CultureInfo.InvariantCulture, n) Then Return n
        Return defaultValue
    End Function

    Private Function JsonDec(map As Dictionary(Of String, String), key As String,
                             defaultValue As Decimal) As Decimal
        Dim value = JsonStr(map, key)
        Dim n As Decimal = 0D
        If Decimal.TryParse(value, Globalization.NumberStyles.Float,
                            Globalization.CultureInfo.InvariantCulture, n) Then Return n
        Return defaultValue
    End Function

    ''' <summary>
    ''' 语义化版本比较：remote 比 local 新返回 True。
    ''' 逐段比数字，位数不同按 0 补齐；带 v 前缀也能认。
    ''' </summary>
    Public Function IsNewerVersion(remote As String, local As String) As Boolean
        If String.IsNullOrWhiteSpace(remote) Then Return False
        Dim a = VersionParts(remote)
        Dim b = VersionParts(local)

        Dim n = Math.Max(a.Length, b.Length)
        For i = 0 To n - 1
            Dim x = If(i < a.Length, a(i), 0)
            Dim y = If(i < b.Length, b(i), 0)
            If x > y Then Return True
            If x < y Then Return False
        Next
        Return False
    End Function

    Private Function VersionParts(version As String) As Integer()
        Dim list As New List(Of Integer)()
        Dim raw = If(version, "").Trim().TrimStart("v"c, "V"c)
        For Each part In raw.Split("."c)
            Dim n As Integer = 0
            If Integer.TryParse(part.Trim(), n) Then list.Add(n) Else list.Add(0)
        Next
        If list.Count = 0 Then list.Add(0)
        Return list.ToArray()
    End Function

    ' ==================== Logo ====================

    Private _infoTask As Task(Of TreeOSReleaseInfo)
    Private _logoImage As BitmapImage
    Private _logoTask As Task(Of BitmapImage)

    ' 通用图片缓存：cacheKey -> 图片 / 进行中的任务
    Private _imageCache As New Dictionary(Of String, BitmapImage)
    Private _imageTasks As New Dictionary(Of String, Task(Of BitmapImage))

    ''' <summary>
    ''' TreeOS 产品图标（下载页 TreeOS 卡片用）。地址来自 download.txt 的 logo=，
    ''' 首次调用会联网下载并写本地缓存，之后复用内存中的图片；断网回退本地缓存。
    ''' </summary>
    Public Function GetLogoAsync() As Task(Of BitmapImage)
        If _logoImage IsNot Nothing Then
            Return Task.FromResult(_logoImage)
        End If
        If _logoTask Is Nothing Then
            _logoTask = LoadLogoCoreAsync()
        End If
        Return _logoTask
    End Function

    ''' <summary>
    ''' Caelus Studio 工作室图标（首页 / 标题栏用），地址固定为官网图片。
    ''' </summary>
    Public Function GetStudioLogoAsync() As Task(Of BitmapImage)
        Return GetImageAsync(New String() {StudioLogoUrl}, "studio-logo")
    End Function

    ''' <summary>
    ''' 按候选地址依次加载图片，带内存 + 磁盘缓存。同一个 cacheKey 只会真正下载一次。
    ''' </summary>
    Public Function GetImageAsync(urls As String(), cacheKey As String) As Task(Of BitmapImage)
        Dim cached As BitmapImage = Nothing
        If _imageCache.TryGetValue(cacheKey, cached) Then
            Return Task.FromResult(cached)
        End If

        Dim running As Task(Of BitmapImage) = Nothing
        If _imageTasks.TryGetValue(cacheKey, running) Then
            Return running
        End If

        running = LoadImageCoreAsync(urls, cacheKey)
        _imageTasks(cacheKey) = running
        Return running
    End Function

    Private Async Function LoadImageCoreAsync(urls As String(), cacheKey As String) As Task(Of BitmapImage)
        Dim list As New List(Of String)
        For Each u In urls
            If Not String.IsNullOrWhiteSpace(u) Then list.Add(u.Trim())
        Next

        Dim data = Await FetchImageBytesAsync(list, cacheKey & ".png")

        If data Is Nothing OrElse data.Length = 0 Then
            _imageTasks.Remove(cacheKey)     ' 允许下次重试
            Return Nothing
        End If

        Dim img = CreateBitmapImage(data)
        _imageCache(cacheKey) = img
        Return img
    End Function

    Private Async Function LoadLogoCoreAsync() As Task(Of BitmapImage)
        Dim urls As New List(Of String)

        ' 1) 稳定镜像优先。download.txt 里的 logo= 目前指向 raw.githubusercontent，
        '    在国内连不通，放它前面会白白拖慢；所以先试 jsDelivr 和 GitHub Pages。
        For Each u In New String() {LogoMirrorUrl, LogoPagesUrl}
            urls.Add(u)
        Next

        ' 2) download.txt 中 logo= 指定的地址（可配置项）
        Try
            Dim info = Await GetReleaseInfoAsync(False)
            If info IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(info.Logo) Then
                Dim custom = info.Logo.Trim()
                If Not urls.Contains(custom) Then urls.Add(custom)
            End If
        Catch
        End Try

        ' 3) raw 兜底
        For Each u In New String() {DefaultLogoUrl, LogoPngUrl}
            If Not urls.Contains(u) Then urls.Add(u)
        Next

        Dim data = Await FetchImageBytesAsync(urls, "logo.png")

        If data Is Nothing OrElse data.Length = 0 Then
            _logoTask = Nothing      ' 允许下次重试
            Return Nothing
        End If

        _logoImage = CreateBitmapImage(data)
        Return _logoImage
    End Function

    ''' <summary>
    ''' 按候选地址依次尝试下载图片字节，成功则写本地缓存，全部失败则回退本地缓存。
    ''' </summary>
    Private Async Function FetchImageBytesAsync(urls As List(Of String), cacheFileName As String) As Task(Of Byte())
        Dim cachePath = GetCachePath(cacheFileName)
        Dim data As Byte() = Nothing

        For Each u In urls
            Try
                Dim raw = Await DownloadBytesAsync(u)
                Dim image = CoerceToImageBytes(raw)
                If image IsNot Nothing Then
                    data = image
                    Exit For
                End If
            Catch
            End Try
        Next

        If data Is Nothing Then
            Try
                If File.Exists(cachePath) Then data = File.ReadAllBytes(cachePath)
            Catch
            End Try
        Else
            Try
                File.WriteAllBytes(cachePath, data)
            Catch
            End Try
        End If

        Return data
    End Function

    Private Async Function DownloadBytesAsync(url As String) As Task(Of Byte())
        Dim sep As String = If(url.Contains("?"), "&", "?")
        Dim full = url & sep & "t=" & DateTime.UtcNow.Ticks.ToString()

        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(20)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")
            Dim response = Await client.GetAsync(full)
            If Not response.IsSuccessStatusCode Then
                Throw New Exception("HTTP " & CInt(response.StatusCode).ToString())
            End If
            Return Await response.Content.ReadAsByteArrayAsync()
        End Using
    End Function

    ''' <summary>
    ''' 仓库里既可能是真正的图片，也可能是 base64 文本（MCP 通道无法直传二进制，
    ''' 所以 logo.txt 里存的是 base64）。这里两种都认。
    ''' 关键是：判断图片必须看文件头，不能看扩展名 —— Logo.png 实际就是 JPEG。
    ''' </summary>
    Private Function CoerceToImageBytes(data As Byte()) As Byte()
        If data Is Nothing OrElse data.Length = 0 Then Return Nothing
        If IsImage(data) Then Return data

        Dim text As String
        Try
            text = Encoding.UTF8.GetString(data)
        Catch
            Return Nothing
        End Try

        ' 兼容 data URI 前缀
        Dim i = text.IndexOf("base64,")
        If i >= 0 Then text = text.Substring(i + 7)

        ' 去掉注释行和所有空白
        Dim sb As New StringBuilder()
        For Each line In text.Split(New Char() {ChrW(10), ChrW(13)})
            If line.Trim().StartsWith("#") Then Continue For
            For Each ch In line
                If Not Char.IsWhiteSpace(ch) Then sb.Append(ch)
            Next
        Next

        Try
            Dim decoded = Convert.FromBase64String(sb.ToString())
            If IsImage(decoded) Then Return decoded
        Catch
        End Try

        Return Nothing
    End Function

    ''' <summary>
    ''' 按文件头魔数判断是否为图片。不能只看 PNG —— 仓库里的文件可能名不副实
    ''' （例如 Logo.png 实际是 JPEG），WPF 是按内容解码的，所以这里也要按内容判断。
    ''' </summary>
    Private Function IsImage(data As Byte()) As Boolean
        If data Is Nothing OrElse data.Length < 12 Then Return False

        ' PNG  89 50 4E 47
        If data(0) = &H89 AndAlso data(1) = &H50 AndAlso
           data(2) = &H4E AndAlso data(3) = &H47 Then Return True

        ' JPEG FF D8
        If data(0) = &HFF AndAlso data(1) = &HD8 Then Return True

        ' GIF  47 49 46
        If data(0) = &H47 AndAlso data(1) = &H49 AndAlso data(2) = &H46 Then Return True

        ' BMP  42 4D
        If data(0) = &H42 AndAlso data(1) = &H4D Then Return True

        ' ICO  00 00 01 00
        If data(0) = 0 AndAlso data(1) = 0 AndAlso data(2) = 1 AndAlso data(3) = 0 Then Return True

        ' WEBP RIFF .... WEBP
        If data(0) = &H52 AndAlso data(1) = &H49 AndAlso data(2) = &H46 AndAlso data(3) = &H46 AndAlso
           data(8) = &H57 AndAlso data(9) = &H45 AndAlso data(10) = &H42 AndAlso data(11) = &H50 Then Return True

        Return False
    End Function

    Private Function CreateBitmapImage(data As Byte()) As BitmapImage
        Using ms As New MemoryStream(data)
            Dim img As New BitmapImage()
            img.BeginInit()
            img.CacheOption = BitmapCacheOption.OnLoad
            img.StreamSource = ms
            img.EndInit()
            img.Freeze()
            Return img
        End Using
    End Function

    Public Function GetAppDataFolder() As String
        Dim baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        If String.IsNullOrEmpty(baseDir) Then baseDir = System.IO.Path.GetTempPath()
        Dim dir = System.IO.Path.Combine(baseDir, "Caelus Studio")
        Try
            Directory.CreateDirectory(dir)
        Catch
        End Try
        Return dir
    End Function

    Private Function GetCachePath(fileName As String) As String
        Return System.IO.Path.Combine(GetAppDataFolder(), fileName)
    End Function

    ''' <summary>
    ''' 带实时进度的文件下载
    ''' </summary>
    Public Async Function DownloadAsync(url As String,
                                        destPath As String,
                                        progress As IProgress(Of DownloadReport),
                                        token As CancellationToken) As Task
        EnsureTls()

        Using client As New HttpClient()
            client.Timeout = Timeout.InfiniteTimeSpan
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")

            Using response = Await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                If Not response.IsSuccessStatusCode Then
                    Throw New Exception("下载失败：服务器返回 HTTP " & CInt(response.StatusCode).ToString())
                End If

                Dim total As Long = -1
                If response.Content.Headers.ContentLength.HasValue Then
                    total = response.Content.Headers.ContentLength.Value
                End If

                Using httpStream = Await response.Content.ReadAsStreamAsync()
                    Using fileStream As New FileStream(destPath, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, 8192, True)
                        Dim buffer(8191) As Byte
                        Dim received As Long = 0
                        Dim sw = Stopwatch.StartNew()
                        Dim lastBytes As Long = 0
                        Dim lastMs As Long = 0

                        Do
                            token.ThrowIfCancellationRequested()

                            Dim read = Await httpStream.ReadAsync(buffer, 0, buffer.Length, token)
                            If read <= 0 Then Exit Do

                            Await fileStream.WriteAsync(buffer, 0, read, token)
                            received += read

                            Dim elapsed = sw.ElapsedMilliseconds
                            If elapsed - lastMs >= 250 Then
                                Dim delta = elapsed - lastMs
                                Dim speed As Double = 0
                                If delta > 0 Then
                                    speed = (received - lastBytes) / (delta / 1000.0R)
                                End If
                                lastBytes = received
                                lastMs = elapsed

                                If progress IsNot Nothing Then
                                    progress.Report(New DownloadReport With {
                                        .Received = received,
                                        .Total = total,
                                        .Speed = speed
                                    })
                                End If
                            End If
                        Loop

                        If progress IsNot Nothing Then
                            progress.Report(New DownloadReport With {
                                .Received = received,
                                .Total = total,
                                .Speed = 0
                            })
                        End If
                    End Using
                End Using
            End Using
        End Using
    End Function

    ''' <summary>
    ''' 探测远端文件大小（字节）。用来在下载前就把「文件大小」显示出来。
    ''' 先发 HEAD 读 Content-Length；服务器不给就退回 GET（只读响应头、不读 body）。
    ''' 拿不到一律返回 -1，调用方保持原样显示即可。
    ''' </summary>
    Public Async Function GetRemoteFileSizeAsync(url As String) As Task(Of Long)
        If String.IsNullOrWhiteSpace(url) Then Return -1L
        EnsureTls()

        ' ---- 1) HEAD ----
        Try
            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(15)
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")

                Using request As New HttpRequestMessage(HttpMethod.Head, url)
                    Using response = Await client.SendAsync(request)
                        If response.IsSuccessStatusCode AndAlso
                           response.Content.Headers.ContentLength.HasValue Then
                            Return response.Content.Headers.ContentLength.Value
                        End If
                    End Using
                End Using
            End Using
        Catch
            ' 有些服务端不支持 HEAD，走下面的兜底
        End Try

        ' ---- 2) GET 只取响应头 ----
        Try
            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(20)
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Caelus-Studio/1.0")

                Using response = Await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    If response.IsSuccessStatusCode AndAlso
                       response.Content.Headers.ContentLength.HasValue Then
                        Return response.Content.Headers.ContentLength.Value
                    End If
                End Using
            End Using
        Catch
        End Try

        Return -1L
    End Function

    ''' <summary>
    ''' 从 URL 推断文件名
    ''' </summary>
    Public Function GuessFileName(url As String, fallback As String) As String
        Try
            Dim uri As New Uri(url)
            Dim name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))
            If String.IsNullOrWhiteSpace(name) Then Return fallback
            For Each c In Path.GetInvalidFileNameChars()
                name = name.Replace(c, "_"c)
            Next
            Return name
        Catch
            Return fallback
        End Try
    End Function

    ''' <summary>
    ''' 若文件已存在，追加 (1) (2) ...
    ''' </summary>
    Public Function UniquePath(path As String) As String
        If Not File.Exists(path) Then Return path
        Dim dir = System.IO.Path.GetDirectoryName(path)
        Dim name = System.IO.Path.GetFileNameWithoutExtension(path)
        Dim ext = System.IO.Path.GetExtension(path)
        Dim i As Integer = 1
        Dim candidate As String
        Do
            candidate = System.IO.Path.Combine(dir, name & " (" & i.ToString() & ")" & ext)
            i += 1
        Loop While File.Exists(candidate)
        Return candidate
    End Function

    ' ==================== 下载目录 ====================

    Private Const DownloadFolderFileName As String = "download-folder.txt"

    ''' <summary>自定义下载目录的保存位置</summary>
    Private Function DownloadFolderFilePath() As String
        Return GetCachePath(DownloadFolderFileName)
    End Function

    ''' <summary>读取用户自定义的下载目录；未设置或已失效返回空串</summary>
    Public Function LoadDownloadFolder() As String
        Try
            ' 局部变量不能叫 file / path，会遮蔽 System.IO.File / System.IO.Path
            Dim cfg = DownloadFolderFilePath()
            If System.IO.File.Exists(cfg) Then
                Dim text = System.IO.File.ReadAllText(cfg).Trim()
                If text.Length > 0 AndAlso Directory.Exists(text) Then Return text
            End If
        Catch
        End Try
        Return ""
    End Function

    ''' <summary>保存自定义下载目录；传空串表示恢复默认</summary>
    Public Sub SaveDownloadFolder(folder As String)
        Try
            Dim cfg = DownloadFolderFilePath()
            If String.IsNullOrWhiteSpace(folder) Then
                If System.IO.File.Exists(cfg) Then System.IO.File.Delete(cfg)
            Else
                System.IO.File.WriteAllText(cfg, folder)
            End If
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 实际使用的下载目录：优先用户自定义，否则系统「下载」目录，并确保目录存在。
    ''' </summary>
    Public Function GetDownloadFolder() As String
        Dim folder = LoadDownloadFolder()
        If String.IsNullOrEmpty(folder) Then folder = DefaultDownloadFolder()

        Try
            If Not Directory.Exists(folder) Then Directory.CreateDirectory(folder)
        Catch
        End Try
        Return folder
    End Function

    ''' <summary>
    ''' 系统「下载」目录。VB 的 SpecialDirectories 没有 Downloads 成员，
    ''' 因此走 Windows 已知文件夹 API（FOLDERID_Downloads），失败再逐级回退。
    ''' </summary>
    Public Function DefaultDownloadFolder() As String
        Dim folder As String = ""
        Try
            folder = KnownFolders.GetDownloads()
        Catch
            folder = ""
        End Try

        If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then
            folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        End If
        If Not Directory.Exists(folder) Then
            folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        End If
        Return folder
    End Function

    Private Class KnownFolders

        ' FOLDERID_Downloads
        Private Shared ReadOnly DownloadsId As New Guid("374DE290-123F-4565-9164-39C4925E467B")

        <DllImport("shell32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
        Private Shared Function SHGetKnownFolderPath(<MarshalAs(UnmanagedType.LPStruct)> rfid As Guid,
                                                     dwFlags As Integer,
                                                     hToken As IntPtr,
                                                     ByRef ppszPath As IntPtr) As Integer
        End Function

        Public Shared Function GetDownloads() As String
            Dim pPath As IntPtr = IntPtr.Zero
            Try
                Dim hr = SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, pPath)
                If hr <> 0 OrElse pPath = IntPtr.Zero Then Return ""
                Return Marshal.PtrToStringUni(pPath)
            Finally
                If pPath <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pPath)
            End Try
        End Function

    End Class

    Public Function FormatBytes(bytes As Double) As String
        If bytes < 0 Then Return "--"
        Dim units = New String() {"B", "KB", "MB", "GB", "TB"}
        Dim i As Integer = 0
        While bytes >= 1024 AndAlso i < units.Length - 1
            bytes = bytes / 1024
            i += 1
        End While
        Return bytes.ToString("0.##") & " " & units(i)
    End Function

    Public Function FormatSpeed(bytesPerSecond As Double) As String
        If bytesPerSecond <= 0 Then Return ""
        Return FormatBytes(bytesPerSecond) & "/s"
    End Function

    Public Sub OpenUrl(url As String)
        Try
            Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})
        Catch
        End Try
    End Sub

    Public Sub RevealInExplorer(path As String)
        Try
            If File.Exists(path) Then
                Process.Start("explorer.exe", "/select,""" & path & """")
            ElseIf Directory.Exists(path) Then
                Process.Start("explorer.exe", """" & path & """")
            End If
        Catch
        End Try
    End Sub

End Module
