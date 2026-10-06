Imports System.Net
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Runtime.InteropServices

Class MainWindow

    Private _currentNav As Button

    Public Sub New()
        InitializeComponent()

        Try
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
        Catch
        End Try

        ' Frame 自带导航快捷键：F5 = 刷新、Backspace / Alt+← → = 前进后退。
        ' 我们是直接 Navigate(page 实例) 的对象导航，Frame.Refresh() 在这种模式下会抛异常导致崩溃，
        ' 所以把导航命令全部摘掉。
        Try
            MainFrame.CommandBindings.Clear()
        Catch
        End Try
    End Sub

    Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
        ' 圆角（分层）窗口要显式补一次任务栏图标，否则任务栏是空白的
        EnsureTaskbarIcon()
        ForceTaskbarButtonRebuild()

        NavigateTo(btnNavHome, New HomePage())
        LoadBrandLogoAsync()
        AnnounceFinishedUpdate()
        ScheduleUpdateCleanup()

        ' 恢复上次的 Star ID 会话
        Try
            UpdateStarIdUi(Await StarIDSource.TryRestoreAsync())
        Catch
        End Try

        ' 云端管理台是否发布了新版本
        Await CheckAppUpdateAsync()
    End Sub

    ''' <summary>
    ''' 窗口重新获得焦点时刷新当前页的云端配置 —— 在浏览器改完管理台切回来就能看到变化，
    ''' 不用重启应用。只有下载页和商店页用得上，其它页面直接忽略。
    ''' （这个事件只在 XAML 里绑定，VB 侧不要再写 Handles，否则会跑两遍）
    ''' </summary>
    Private Async Sub Window_Activated(sender As Object, e As EventArgs)
        Dim download = TryCast(MainFrame.Content, DownloadPage)
        If download IsNot Nothing Then
            Await download.RefreshCloudConfigAsync()
            Return
        End If

        Dim shop = TryCast(MainFrame.Content, ShopPage)
        If shop IsNot Nothing Then
            Await shop.RefreshShopAsync()
        End If
    End Sub

    ' ---------- 任务栏图标 ----------

    Private Const WM_SETICON As Integer = &H80
    Private Const ICON_SMALL As Integer = 0
    Private Const ICON_BIG As Integer = 1

    <DllImport("user32.dll", CharSet:=CharSet.Auto)>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer,
                                       wParam As IntPtr, lParam As IntPtr) As IntPtr
    End Function

    <DllImport("user32.dll")>
    Private Shared Function CreateIconFromResourceEx(pbIconBits As Byte(), cbIconBits As UInteger,
                                                     fIcon As Boolean, dwVer As UInteger,
                                                     cxDesired As UInteger, cyDesired As UInteger,
                                                     flags As UInteger) As IntPtr
    End Function

    <DllImport("user32.dll", EntryPoint:="SetClassLongPtrW")>
    Private Shared Function SetClassLongPtr(hWnd As IntPtr, nIndex As Integer, dwNewLong As IntPtr) As IntPtr
    End Function

    Private Const GCLP_HICON As Integer = -10
    Private Const GCLP_HICONSM As Integer = -34

    ''' <summary>
    ''' 圆角窗口是分层窗口（AllowsTransparency=True），WPF 在这条路径上有时不会把图标交给任务栏，
    ''' 表现就是任务栏按钮变成空白/默认图标，而 XAML 里的 Icon 其实一直都在。
    ''' 实测（WM_GETICON 探针）：窗口的大/小图标其实都设置上了，唯独「类小图标」GCLP_HICONSM 是 0，
    ''' Win11 任务栏在某些路径下会读到这个空值 → 显示空白。
    ''' 所以这里三管齐下：WM_SETICON（大+小）+ SetClassLongPtr 补类图标 + 强制任务栏重建按钮。
    ''' 图标来源改成**应用内置的 logo.ico 资源**（pack URI），不再从 exe 文件路径抠，
    ''' 和「任务栏图标固定用内置 logo，不随仓库 logo 变动」的约定完全一致。
    ''' </summary>
    Private Sub EnsureTaskbarIcon()
        Try
            Dim hwnd = New WindowInteropHelper(Me).Handle
            If hwnd = IntPtr.Zero Then Return

            Dim big = HiconFromEmbeddedIco(32)
            Dim small = HiconFromEmbeddedIco(16)
            If big = IntPtr.Zero AndAlso small = IntPtr.Zero Then Return

            ' 1) 窗口图标（大 + 小都发，任务栏用的是小图标）
            If big <> IntPtr.Zero Then SendMessage(hwnd, WM_SETICON, New IntPtr(ICON_BIG), big)
            If small <> IntPtr.Zero Then SendMessage(hwnd, WM_SETICON, New IntPtr(ICON_SMALL), small)

            ' 2) 类图标也要补：实测 GCLP_HICONSM 是 0，有的任务栏路径读它
            If big <> IntPtr.Zero Then SetClassLongPtr(hwnd, GCLP_HICON, big)
            If small <> IntPtr.Zero Then SetClassLongPtr(hwnd, GCLP_HICONSM, small)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 从内置 logo.ico 资源里挑最接近 wantSize 的那一帧，转成 HICON。
    ''' ICO 结构：ICONDIR(6 字节) + N 个 ICONDIRENTRY(16 字节：宽/高/色板/保留/平面数/位深/数据长度/数据偏移)。
    ''' 宽或高为 0 表示 256。
    ''' </summary>
    Private Function HiconFromEmbeddedIco(wantSize As Integer) As IntPtr
        Try
            Dim sri = System.Windows.Application.GetResourceStream(
                New Uri("pack://application:,,,/logo.ico"))
            If sri Is Nothing Then Return IntPtr.Zero

            Using ms As New System.IO.MemoryStream()
                sri.Stream.CopyTo(ms)
                Dim bytes = ms.ToArray()

                Dim count = BitConverter.ToUInt16(bytes, 4)
                Dim bestIdx As Integer = -1
                Dim bestDiff As Integer = Integer.MaxValue

                For i = 0 To count - 1
                    Dim e = 6 + i * 16
                    Dim w = CInt(bytes(e)) : If w = 0 Then w = 256
                    Dim h = CInt(bytes(e + 1)) : If h = 0 Then h = 256
                    Dim diff = Math.Abs(w - wantSize) + Math.Abs(h - wantSize)
                    If diff < bestDiff Then
                        bestDiff = diff
                        bestIdx = i
                    End If
                Next
                If bestIdx < 0 Then Return IntPtr.Zero

                Dim entry = 6 + bestIdx * 16
                Dim dataSize = BitConverter.ToUInt32(bytes, entry + 8)
                Dim dataOffset = BitConverter.ToUInt32(bytes, entry + 12)
                If dataSize = 0 OrElse CLng(dataOffset) + CLng(dataSize) > bytes.Length Then Return IntPtr.Zero

                Dim frame(CInt(dataSize) - 1) As Byte
                Array.Copy(bytes, CLng(dataOffset), frame, 0, CInt(dataSize))

                ' fIcon=True 图标；dwVer=0x00030000；尺寸参数传 0 让系统用资源自带的
                Return CreateIconFromResourceEx(frame, dataSize, True, &H30000UI, 0UI, 0UI, 0UI)
            End Using
        Catch
            Return IntPtr.Zero
        End Try
    End Function

    ''' <summary>
    ''' 强制任务栏重建本窗口的按钮：ShowInTaskbar 切一遍会导致 WPF 重建窗口句柄，
    ''' 重建后 WPF 会用 Icon 属性重新走一遍图标流程，新按钮就能拿到图标。
    ''' 代价是任务栏按钮闪一下，只在启动时做一次。
    ''' </summary>
    Private Sub ForceTaskbarButtonRebuild()
        Try
            Dim shown = ShowInTaskbar
            ShowInTaskbar = Not shown
            ShowInTaskbar = shown
            ' 句柄重建后窗口图标要再补一次
            EnsureTaskbarIcon()
        Catch
        End Try
    End Sub

    ' ---------- 圆角窗口 ----------

    ''' <summary>窗口默认圆角半径（最大化时取消圆角）</summary>
    Private Const WindowCornerRadius As Double = 10

    ''' <summary>
    ''' 尺寸变化后要重算裁剪区：Clip 是绝对坐标的几何图形，不会跟着布局自动变。
    ''' （只在 XAML 里绑定，VB 侧不要再加 Handles）
    ''' </summary>
    Private Sub RootBorder_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        ApplyCornerRadius()
    End Sub

    ''' <summary>
    ''' 最大化时贴满屏幕，留圆角会在屏幕四角露出桌面，所以最大化一律取消圆角。
    ''' （只在 XAML 里绑定，VB 侧不要再加 Handles）
    ''' </summary>
    Private Sub Window_StateChanged(sender As Object, e As EventArgs)
        ApplyCornerRadius()
        ' 最大化/还原会重建窗口样式，图标也可能被系统重置，顺手补一次
        EnsureTaskbarIcon()
    End Sub

    ''' <summary>按当前窗口状态刷新外层 Border 的圆角与裁剪</summary>
    Private Sub ApplyCornerRadius()
        Dim r = If(Me.WindowState = WindowState.Maximized, 0, WindowCornerRadius)
        RoundedWindow.ApplyClip(rootBorder, r)
    End Sub

    ' ---------- 应用更新 ----------

    ''' <summary>
    ''' 上一次自动更新成功后会留一个标记，这里在首次启动时告诉用户一声。
    ''' 没更新过就是空串，什么都不做。
    ''' </summary>
    Private Sub AnnounceFinishedUpdate()
        Try
            Dim version = AppUpdater.TakeDoneFlag()
            If version.Length = 0 Then Return
            MessageBox.Show(Me, "Caelus Studio 已更新到 " & version & "。", "更新完成",
                            MessageBoxButton.OK, MessageBoxImage.Information)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 清理上次更新留下的备份文件。延后几秒执行，等旧进程的句柄彻底释放。
    ''' </summary>
    Private Async Sub ScheduleUpdateCleanup()
        Try
            Await Task.Delay(3000)
            AppUpdater.CleanupLeftovers()
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 启动时比对云端管理台下发的最新版本号。
    ''' 读不到配置、或版本没变化，就完全静默——更新检查绝不能拦住启动。
    ''' </summary>
    Private Async Function CheckAppUpdateAsync() As Task
        Try
            Dim cfg = Await TreeOSSource.GetAppConfigAsync(True)
            If cfg Is Nothing OrElse Not cfg.Loaded Then Return

            ' ---- 1) 回退优先 ----
            ' 当前版本比回退目标新才需要回退（否则用户已经是旧版本，不用管）
            If cfg.AppRollback Then
                Dim target = If(cfg.AppRollbackTo, "").Trim()
                If target.Length > 0 AndAlso
                   TreeOSSource.IsNewerVersion(TreeOSSource.LocalAppVersion, target) Then

                    Dim rollback As New UpdateWindow()
                    rollback.Owner = Me
                    rollback.IsRollback = True
                    rollback.NewVersion = target
                    rollback.Notes = cfg.AppRollbackNotes

                    ' 回退包地址留空时退回用安装包地址
                    Dim backUrl = If(cfg.AppRollbackUrl, "").Trim()
                    If backUrl.Length = 0 Then backUrl = If(cfg.AppUrl, "").Trim()
                    rollback.UpdateUrl = backUrl

                    rollback.ShowDialog()

                    ' 下载 → 替换 → 重启全部由更新窗口自己完成，这里只管用户选择退出
                    If rollback.ChoseExit Then Me.Close()

                    ' 需要回退时不再提示更新，避免刚退回旧版又被推上新版本
                    Return
                End If
            End If

            ' ---- 2) 正常更新 ----
            If Not TreeOSSource.IsNewerVersion(cfg.AppVersion, TreeOSSource.LocalAppVersion) Then Return

            Dim dlg As New UpdateWindow()
            dlg.Owner = Me
            dlg.NewVersion = cfg.AppVersion
            dlg.Notes = cfg.AppNotes
            dlg.IsForced = cfg.AppForce
            dlg.UpdateUrl = If(cfg.AppUrl, "").Trim()
            dlg.ShowDialog()

            ' 新版本 exe 的下载与自我替换在窗口内部进行，成功后进程会被替换并重启
            If dlg.ChoseExit Then Me.Close()
        Catch
            ' 忽略：更新检查失败不影响使用
        End Try
    End Function

    ' ---------- 导航 ----------

    Private Sub NavigateTo(navButton As Button, page As Page)
        If _currentNav IsNot Nothing Then
            _currentNav.Tag = Nothing
        End If
        _currentNav = navButton
        navButton.Tag = "Active"
        ShowPage(page)
    End Sub

    ''' <summary>装载页面，并清掉导航历史</summary>
    Private Sub ShowPage(page As Page)
        MainFrame.Navigate(page)

        ' 不保留导航历史，避免退回旧页面实例（页面状态会错乱）
        Try
            If MainFrame.CanGoBack Then MainFrame.RemoveBackEntry()
        Catch
        End Try
    End Sub

    ' ---------- F5 刷新当前页 ----------

    Private Sub Window_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles Me.PreviewKeyDown
        If e.Key = Key.F5 Then
            RefreshCurrentPage()
            e.Handled = True
        End If
    End Sub

    ''' <summary>重建当前页面实例（导航栏高亮保持不变）</summary>
    Private Sub RefreshCurrentPage()
        If _currentNav Is btnNavHome Then
            ShowPage(New HomePage())
        ElseIf _currentNav Is btnNavDownload Then
            ShowPage(New DownloadPage())
        ElseIf _currentNav Is btnNavShop Then
            ShowPage(New ShopPage())
        ElseIf _currentNav Is btnNavSettings Then
            ShowPage(New SettingsPage())
        ElseIf _currentNav Is btnNavAbout Then
            ShowPage(New AboutPage())
        End If
    End Sub

    Private Sub btnNavHome_Click(sender As Object, e As RoutedEventArgs) Handles btnNavHome.Click
        NavigateTo(btnNavHome, New HomePage())
    End Sub

    Private Sub btnNavDownload_Click(sender As Object, e As RoutedEventArgs) Handles btnNavDownload.Click
        NavigateTo(btnNavDownload, New DownloadPage())
    End Sub

    Private Sub btnNavShop_Click(sender As Object, e As RoutedEventArgs) Handles btnNavShop.Click
        NavigateTo(btnNavShop, New ShopPage())
    End Sub

    Private Sub btnNavSettings_Click(sender As Object, e As RoutedEventArgs) Handles btnNavSettings.Click
        NavigateTo(btnNavSettings, New SettingsPage())
    End Sub

    Private Sub btnNavAbout_Click(sender As Object, e As RoutedEventArgs) Handles btnNavAbout.Click
        NavigateTo(btnNavAbout, New AboutPage())
    End Sub

    ''' <summary>标题栏图标，用工作室图标，失败就保留字母占位</summary>
    Private Async Sub LoadBrandLogoAsync()
        Try
            Dim img = Await TreeOSSource.GetStudioLogoAsync()
            If img IsNot Nothing Then
                brushBrandLogo.ImageSource = img
                hostBrandLogo.Visibility = Visibility.Visible
                txtBrandFallback.Visibility = Visibility.Collapsed
                ' 任务栏图标固定用项目内嵌的 logo.ico，不随仓库 logo 变动
            End If
        Catch
        End Try
    End Sub

    ' ---------- Star ID ----------

    Private Sub UpdateStarIdUi(user As StarIDUser)
        If user IsNot Nothing AndAlso user.StarId.Length > 0 Then
            btnStarId.Content = If(user.Nickname.Length > 0, user.Nickname, user.StarId)

            ' 昵称放大加粗（Orange 反馈）：登录态用正文字号，盖过 LinkButton 的 13px
            btnStarId.FontSize = 15
            btnStarId.FontWeight = FontWeights.SemiBold
            btnStarId.SetResourceReference(Button.ForegroundProperty, "TextPrimary")

            ' 悬停即看到全部账号信息：Star ID / 昵称 / 邮箱（含验证状态）/ 注册时间 / 授权范围 / 令牌到期
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine("Star ID：" & user.StarId)
            If user.Nickname.Length > 0 Then sb.AppendLine("昵称：" & user.Nickname)
            If user.Email.Length > 0 Then
                sb.AppendLine("邮箱：" & user.Email & If(user.EmailVerified, "（已验证）", "（未验证）"))
            End If
            If user.CreatedAt > 0 Then
                Try
                    sb.AppendLine("注册时间：" &
                        DateTimeOffset.FromUnixTimeMilliseconds(user.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                Catch
                End Try
            End If
            sb.AppendLine("授权范围：" & StarIDSource.TokenScope)

            Dim exp = StarIDSource.TokenExpiresAt
            If exp > DateTime.MinValue Then sb.AppendLine("令牌到期：" & exp.ToString("yyyy-MM-dd HH:mm"))

            btnStarId.ToolTip = sb.ToString().TrimEnd()
        Else
            btnStarId.Content = "登录 Star ID"
            ' 未登录恢复 LinkButton 的默认外观
            btnStarId.ClearValue(Button.FontSizeProperty)
            btnStarId.ClearValue(Button.FontWeightProperty)
            btnStarId.ClearValue(Button.ForegroundProperty)
            btnStarId.ToolTip = "使用 Caelus Star ID 登录"
        End If
    End Sub

    ''' <summary>
    ''' 供设置页调用：让导航栏上的 Star ID 按钮与当前登录状态同步。
    ''' </summary>
    Public Sub RefreshStarIdUi()
        UpdateStarIdUi(StarIDSource.CurrentUser)
    End Sub

    ''' <summary>未登录时把与账号相关的菜单项灰掉</summary>
    Private Sub btnStarId_MenuOpening(sender As Object, e As RoutedEventArgs)
        Dim loggedIn = StarIDSource.IsLoggedIn()
        miCopyStarId.IsEnabled = loggedIn
        miLogout.IsEnabled = loggedIn
    End Sub

    Private Async Sub btnStarId_Click(sender As Object, e As RoutedEventArgs) Handles btnStarId.Click
        ' 已登录：弹出菜单（复制 Star ID / 退出登录）
        If StarIDSource.IsLoggedIn() Then
            btnStarId.ContextMenu.IsOpen = True
            Return
        End If

        ' client_id 已内置（StarIDSource.DefaultClientId），IsConfigured 恒为 True，
        ' 不再需要「先配置再登录」的分支（1.2.0 起配置入口已整体移除）

        btnStarId.Content = "正在登录…"
        btnStarId.IsEnabled = False
        Try
            Dim user = Await StarIDSource.LoginAsync()
            If user Is Nothing Then
                MessageBox.Show("登录已取消或超时。", "Caelus Studio",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            End If
            UpdateStarIdUi(user)
        Catch ex As Exception
            ' 把内层异常也带上：网络类错误真正的线索通常在里面（SocketException 之类）
            Dim detail = ex.Message
            If ex.InnerException IsNot Nothing Then
                detail &= vbCrLf & "（" & ex.InnerException.Message & "）"
            End If
            MessageBox.Show("Star ID 登录失败：" & detail, "Caelus Studio",
                            MessageBoxButton.OK, MessageBoxImage.Warning)
            UpdateStarIdUi(StarIDSource.CurrentUser)
        Finally
            btnStarId.IsEnabled = True
        End Try
    End Sub

    Private Sub miCopyStarId_Click(sender As Object, e As RoutedEventArgs) Handles miCopyStarId.Click
        If StarIDSource.IsLoggedIn() Then
            Clipboard.SetText(StarIDSource.CurrentUser.StarId)
        End If
    End Sub

    Private Async Sub miLogout_Click(sender As Object, e As RoutedEventArgs) Handles miLogout.Click
        Await StarIDSource.LogoutAsync()
        UpdateStarIdUi(Nothing)
    End Sub

    ' ---------- 外链 ----------

    Private Sub btnWebsite_Click(sender As Object, e As RoutedEventArgs) Handles btnWebsite.Click
        TreeOSSource.OpenUrl("https://www.caelus.top")
    End Sub

    Private Sub btnGithub_Click(sender As Object, e As RoutedEventArgs) Handles btnGithub.Click
        TreeOSSource.OpenUrl(TreeOSSource.RepositoryUrl)
    End Sub

    ' ---------- 窗口控制 ----------

    ' 说明：标题栏拖拽与双击最大化已由 WindowChrome（CaptionHeight="40"）接管，
    ' 不再需要手动调用 DragMove()

    Private Sub btnMinimize_Click(sender As Object, e As RoutedEventArgs) Handles btnMinimize.Click
        Me.WindowState = WindowState.Minimized
    End Sub

    Private Sub btnClose_Click(sender As Object, e As RoutedEventArgs) Handles btnClose.Click
        Me.Close()
    End Sub

End Class
