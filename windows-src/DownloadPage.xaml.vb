Imports System.Collections.Generic
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading

''' <summary>
''' 下载作品页：两张 DownloadCard，下载逻辑都在卡片自己身上。
''' 下载直链都是内置常量（见 TreeOSSource），不依赖网络配置，打开即可用。
'''
''' 云端配置的刷新时机（不用重启应用）：
'''   1. 进入本页          —— 强制拉最新
'''   2. 停留在本页        —— 每 30 秒轮询一次
'''   3. 窗口重新获得焦点  —— 由 MainWindow 调 RefreshCloudConfigAsync（5 秒节流）
''' </summary>
Class DownloadPage

    ' 云端管理台是否已经下发了配置。已下发时 download.txt 不再参与，避免两边打架
    Private _cloudApplied As Boolean = False

    Private _pollTimer As DispatcherTimer
    Private _lastSync As DateTime = DateTime.MinValue

    ' 停留在本页时的轮询间隔
    Private Shared ReadOnly PollInterval As TimeSpan = TimeSpan.FromSeconds(30)
    ' 窗口切回来时的最小刷新间隔，避免切一下就发一次请求
    Private Shared ReadOnly MinSyncInterval As TimeSpan = TimeSpan.FromSeconds(5)

    Public Sub New()
        InitializeComponent()

        ' TreeOS
        cardTreeOs.SetDownloadUrls(New String() {TreeOSSource.TreeOsUrl})

        ' CaelusOS
        cardCaelusOs.LogoUrl = TreeOSSource.CaelusOsLogoUrl
        cardCaelusOs.SetDownloadUrls(New String() {TreeOSSource.CaelusOsUrl})
    End Sub

    Private Async Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ' 1) 云端管理台配置：下载开关 / 版本号 / 直链，由管理页在网页上控制。
        '    强制刷新，保证每次进来拿到的都是最新值
        Await ApplyCloudConfigAsync(True)

        ' 2) 开始轮询，之后管理台改了配置不用重启应用
        StartPolling()

        ' 3) 可选：仓库 download.txt 里如果还留着 url=，就追加成备用候选；
        '    同时用里面的 version= 更新版本标签。云端已下发配置时整段跳过。
        If _cloudApplied Then Return

        Try
            Dim info = Await TreeOSSource.GetReleaseInfoAsync(False)
            If info Is Nothing Then Return

            If Not String.IsNullOrWhiteSpace(info.Version) Then
                cardTreeOs.VersionText = info.Version
                cardTreeOs.Refresh()
            End If

            If Not String.IsNullOrWhiteSpace(info.Url) AndAlso
               Not String.Equals(info.Url.Trim(), TreeOSSource.TreeOsUrl, StringComparison.OrdinalIgnoreCase) Then

                Dim urls As New List(Of String)
                urls.Add(TreeOSSource.TreeOsUrl)
                urls.Add(info.Url.Trim())
                cardTreeOs.SetDownloadUrls(urls.ToArray())
            End If
        Catch
            ' 配置读取失败不影响下载：内置地址已经就位
        End Try
    End Sub

    ''' <summary>离开页面就停掉轮询，别在后台空转</summary>
    Private Sub Page_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        StopPolling()
    End Sub

    ' ==================== 实时同步 ====================

    Private Sub StartPolling()
        If _pollTimer IsNot Nothing Then Return
        _pollTimer = New DispatcherTimer()
        _pollTimer.Interval = PollInterval
        AddHandler _pollTimer.Tick, AddressOf OnPollTick
        _pollTimer.Start()
    End Sub

    Private Sub StopPolling()
        If _pollTimer Is Nothing Then Return
        _pollTimer.Stop()
        RemoveHandler _pollTimer.Tick, AddressOf OnPollTick
        _pollTimer = Nothing
    End Sub

    Private Async Sub OnPollTick(sender As Object, e As EventArgs)
        Await ApplyCloudConfigAsync(True)
    End Sub

    ''' <summary>
    ''' 供 MainWindow 在窗口重新获得焦点时调用，带节流。
    ''' 这样「在浏览器改完配置 → 切回应用」可以立刻看到变化，不用等轮询。
    ''' </summary>
    Public Async Function RefreshCloudConfigAsync() As Task
        If (DateTime.Now - _lastSync) < MinSyncInterval Then Return
        Await ApplyCloudConfigAsync(True)
    End Function

    ''' <summary>
    ''' 应用云端管理台下发的配置。读不到（断网 / 表没初始化）就什么都不做，
    ''' 卡片保持构造时设置的内置地址，功能不受影响。
    ''' </summary>
    Private Async Function ApplyCloudConfigAsync(forceRefresh As Boolean) As Task
        Try
            Dim cfg = Await TreeOSSource.GetAppConfigAsync(forceRefresh)
            If cfg Is Nothing OrElse Not cfg.Loaded Then Return

            ApplyToCard(cardTreeOs, cfg.TreeOsEnabled, cfg.TreeOsVersion,
                        cfg.TreeOsUrl, cfg.TreeOsNote, TreeOSSource.TreeOsUrl)
            ApplyToCard(cardCaelusOs, cfg.CaelusOsEnabled, cfg.CaelusOsVersion,
                        cfg.CaelusOsUrl, cfg.CaelusOsNote, TreeOSSource.CaelusOsUrl)

            _cloudApplied = True
            _lastSync = DateTime.Now
            ShowSyncTime()
        Catch
            ' 云端配置失败一律忽略，不影响离线下载
        End Try
    End Function

    Private Sub ShowSyncTime()
        txtSync.Text = "云端配置已同步 · " & DateTime.Now.ToString("HH:mm:ss")
        txtSync.Visibility = Visibility.Visible
    End Sub

    ''' <summary>
    ''' 把云端的一张卡片配置完整刷上去。注意每次都全量同步（包括清空的情况），
    ''' 否则在管理台把某个值删掉后，界面会一直留着旧值。
    ''' </summary>
    Private Sub ApplyToCard(card As DownloadCard, enabled As Boolean, version As String,
                            url As String, note As String, fallbackUrl As String)
        ' 版本：云端没填就回到默认文案
        Dim v = If(version, "").Trim()
        card.VersionText = If(v.Length > 0, v, "最新版本")

        ' 直链：云端没填就只用内置地址
        Dim u = If(url, "").Trim()
        If u.Length > 0 Then
            card.SetDownloadUrls(New String() {u, fallbackUrl})
        Else
            card.SetDownloadUrls(New String() {fallbackUrl})
        End If

        card.Refresh()

        ' 开关：开和关都要处理，管理台改回「允许下载」时要能恢复
        If enabled Then
            card.SetEnabled()
        Else
            card.SetDisabled(note)
        End If
    End Sub

    ' 下载位置相关的设置已移到「设置」页（SettingsPage），这里不再重复

    ''' <summary>
    ''' 入场动画结束后必须清掉动画值和 RenderTransform。
    ''' 否则残留的合成层会让整页文字走灰阶抗锯齿，看起来发虚。
    ''' </summary>
    Private Sub EnterAnim_Completed(sender As Object, e As EventArgs)
        Try
            Me.BeginAnimation(OpacityProperty, Nothing)

            Dim t = TryCast(Me.RenderTransform, TranslateTransform)
            If t IsNot Nothing Then
                t.BeginAnimation(TranslateTransform.YProperty, Nothing)
                Me.RenderTransform = Nothing
            End If
        Catch
        End Try
    End Sub

End Class
