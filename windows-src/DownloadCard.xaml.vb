Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>
''' 一张「作品下载卡」：图标 + 名称标签 + 描述 + 进度 + 下载/取消/打开。
''' 用法：在 XAML 里设属性，或代码里改完属性后调 Refresh()。
''' </summary>
Class DownloadCard

    Private _urls As String() = New String() {}
    Private _cts As CancellationTokenSource
    Private _isDownloading As Boolean = False
    Private _finishedPath As String = ""
    Private _ready As Boolean = False
    ' 非空表示云端关掉了这个作品的下载，值就是要显示的提示文案
    Private _disabledNote As String = ""
    ' 已经探测过大小的地址，避免页面轮询时反复发请求
    Private _lastSizeUrl As String = ""

    ' ==================== 外部可设属性 ====================

    Public Property ItemName As String = ""
    Public Property VersionText As String = "最新版本"
    Public Property CategoryText As String = "PPT 系统"
    Public Property DescriptionText As String = ""
    Public Property FallbackLetter As String = "?"

    ''' <summary>图标地址。留空则用 TreeOSSource.GetLogoAsync()（即仓库配置里的 TreeOS 图标）</summary>
    Public Property LogoUrl As String = ""

    ''' <summary>卡片底部的来源说明，留空则不显示</summary>
    Public Property SourceNote As String = ""

    ' ==================== 生命周期 ====================

    Private Async Sub Card_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Refresh()
        UpdateReadyState()

        ' 图标和「文件大小」互不依赖，并行取，谁先好谁先显示
        Dim logoTask = LoadLogoAsync()
        Await ProbeSizeAsync()
        Await logoTask
    End Sub

    ''' <summary>把属性值刷到界面上（属性是代码里改的就调一下）</summary>
    Public Sub Refresh()
        txtName.Text = ItemName
        txtVersion.Text = VersionText
        txtCategory.Text = CategoryText
        txtDescription.Text = DescriptionText
        txtFallback.Text = FallbackLetter

        If String.IsNullOrEmpty(SourceNote) Then
            txtSource.Visibility = Visibility.Collapsed
        Else
            txtSource.Text = SourceNote
            txtSource.Visibility = Visibility.Visible
        End If

        _ready = True
    End Sub

    ''' <summary>设定下载直链（可给多个候选，按顺序尝试）</summary>
    Public Async Sub SetDownloadUrls(urls As String())
        _urls = If(urls Is Nothing, New String() {}, urls)
        If _ready Then
            UpdateReadyState()
            ' 地址变了就顺手把「文件大小」补上（内部会按地址去重，不会每次轮询都发请求）
            ' VB 不支持丢弃返回值，不 Await 会报 BC42358，所以这里用 Async Sub + Await
            Await ProbeSizeAsync()
        End If
    End Sub

    ''' <summary>显示一条错误信息</summary>
    Public Sub SetError(message As String)
        If _ready Then
            btnDownload.IsEnabled = False
            SetStatus(message, "Danger")
        End If
    End Sub

    ''' <summary>
    ''' 云端管理台关掉下载时使用：按钮置灰并显示提示文案。
    ''' 状态记在字段里而不是立刻刷界面，这样无论它是在卡片 Loaded 之前还是之后调用都生效。
    ''' </summary>
    Public Sub SetDisabled(message As String)
        Dim note = If(message, "").Trim()
        _disabledNote = If(note.Length > 0, note, "暂不开放下载")
        If _ready Then UpdateReadyState()
    End Sub

    Public Sub SetEnabled()
        _disabledNote = ""
        If _ready Then UpdateReadyState()
    End Sub

    ' ==================== 内部 ====================

    Private Async Function LoadLogoAsync() As Task
        Try
            Dim img As System.Windows.Media.Imaging.BitmapImage

            If String.IsNullOrEmpty(LogoUrl) Then
                img = Await TreeOSSource.GetLogoAsync()
            Else
                img = Await TreeOSSource.GetImageAsync(New String() {LogoUrl}, CacheKeyFor(LogoUrl))
            End If

            If img IsNot Nothing Then
                brushLogo.ImageSource = img
                hostLogo.Visibility = Visibility.Visible
                txtFallback.Visibility = Visibility.Collapsed
            End If
        Catch
        End Try
    End Function

    ' ==================== 文件大小 ====================

    ''' <summary>当前第一个可用的下载地址；没有就返回空串</summary>
    Private Function FirstUrl() As String
        If _urls Is Nothing Then Return ""
        For Each item In _urls
            If Not String.IsNullOrWhiteSpace(item) Then Return item.Trim()
        Next
        Return ""
    End Function

    ''' <summary>
    ''' 探测远端文件大小，填到右上角「文件大小」上 —— 下载前就能看到，不用等下载完。
    ''' 同一个地址只探一次：本页每 30 秒轮询会把地址重新设一遍，不能每次都发请求。
    ''' </summary>
    Private Async Function ProbeSizeAsync() As Task
        Try
            Dim target = FirstUrl()
            If target.Length = 0 Then Return
            If String.Equals(target, _lastSizeUrl, StringComparison.OrdinalIgnoreCase) Then Return
            _lastSizeUrl = target

            If txtSize.Text = "--" Then txtSize.Text = "…"

            Dim size = Await TreeOSSource.GetRemoteFileSizeAsync(target)
            If size > 0 Then
                txtSize.Text = TreeOSSource.FormatBytes(size)
            Else
                ' 这次没探到（断网 / 服务端没给长度），清掉记录，下次轮询再试
                _lastSizeUrl = ""
            End If
        Catch
        End Try
    End Function

    ''' <summary>
    ''' 由 URL 生成稳定的磁盘缓存名。
    ''' 注意：别在这里做乘法哈希 —— VB 默认开启整数溢出检查，
    ''' h * 31 累积几次就会抛 OverflowException，图片会永远加载不出来。
    ''' </summary>
    Private Function CacheKeyFor(url As String) As String
        Dim name = url

        ' 去掉查询串
        Dim q = name.IndexOf("?"c)
        If q >= 0 Then name = name.Substring(0, q)

        ' 只取最后一段路径
        Dim slash = name.LastIndexOf("/"c)
        If slash >= 0 Then name = name.Substring(slash + 1)

        ' 只保留文件名安全字符
        Dim sb As New System.Text.StringBuilder()
        For Each ch In name
            If Char.IsLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "-"c OrElse ch = "_"c Then
                sb.Append(ch)
            End If
        Next
        name = sb.ToString()

        If name.Length = 0 Then name = "image"
        If name.Length > 48 Then name = name.Substring(name.Length - 48)
        Return name
    End Function

    Private Sub UpdateReadyState()
        If _isDownloading Then Return

        ' 云端关掉下载时优先：不管地址在不在，按钮都保持置灰
        If _disabledNote.Length > 0 Then
            btnDownload.IsEnabled = False
            btnCancel.Visibility = Visibility.Collapsed
            SetStatus(_disabledNote, "TextMuted")
            Return
        End If

        If _urls Is Nothing OrElse _urls.Length = 0 Then
            btnDownload.IsEnabled = False
            SetStatus("正在读取下载地址…", "TextMuted")
        Else
            btnDownload.IsEnabled = True
            SetStatus("就绪 · 点击「下载」开始下载", "TextMuted")
        End If
    End Sub

    ' ==================== 下载 ====================

    Private Async Sub btnDownload_Click(sender As Object, e As RoutedEventArgs) Handles btnDownload.Click
        If _isDownloading Then Return
        If _urls Is Nothing OrElse _urls.Length = 0 Then Return
        Await StartDownloadAsync()
    End Sub

    Private Async Function StartDownloadAsync() As Task
        _cts = New CancellationTokenSource()
        _isDownloading = True
        SetBusy(True)
        SetStatus("正在连接…", "TextMuted")

        Dim progress As New Progress(Of DownloadReport)(AddressOf OnProgress)
        Dim lastError As String = ""

        Try
            For Each url In _urls
                If String.IsNullOrWhiteSpace(url) Then Continue For

                pbProgress.IsIndeterminate = False
                pbProgress.Value = 0
                txtPercent.Text = "0%"
                txtDetail.Text = ""
                txtSpeed.Text = ""

                Dim folder = TreeOSSource.GetDownloadFolder()
                Dim fileName = TreeOSSource.GuessFileName(url, "download.bin")
                Dim dest = TreeOSSource.UniquePath(Path.Combine(folder, fileName))
                Dim tmp = dest & ".part"

                Try
                    Await TreeOSSource.DownloadAsync(url, tmp, progress, _cts.Token)

                    If File.Exists(tmp) Then
                        If File.Exists(dest) Then File.Delete(dest)
                        File.Move(tmp, dest)
                    End If

                    _finishedPath = dest
                    Dim length = New FileInfo(dest).Length
                    pbProgress.Value = 100
                    txtPercent.Text = "100%"
                    txtSpeed.Text = ""
                    txtDetail.Text = TreeOSSource.FormatBytes(length)
                    txtSize.Text = TreeOSSource.FormatBytes(length)
                    SetStatus("下载完成：" & dest, "Accent")
                    btnOpenFile.IsEnabled = True
                    btnOpenFolder.IsEnabled = True
                    Return

                Catch ex As OperationCanceledException
                    TryDelete(tmp)
                    SetStatus("已取消下载。", "TextMuted")
                    Return

                Catch ex As Exception
                    TryDelete(tmp)
                    lastError = ex.Message
                End Try
            Next

            If Not String.IsNullOrEmpty(lastError) Then
                SetStatus(lastError, "Danger")
            End If

        Finally
            _isDownloading = False
            SetBusy(False)
        End Try
    End Function

    Private Sub OnProgress(report As DownloadReport)
        If report.Total > 0 Then
            Dim pct As Double = report.Received * 100.0R / report.Total
            If pct > 100 Then pct = 100
            pbProgress.IsIndeterminate = False
            pbProgress.Value = pct
            txtPercent.Text = pct.ToString("0.0") & "%"
            txtDetail.Text = TreeOSSource.FormatBytes(report.Received) & " / " & TreeOSSource.FormatBytes(report.Total)
        Else
            pbProgress.IsIndeterminate = True
            txtPercent.Text = "--"
            txtDetail.Text = TreeOSSource.FormatBytes(report.Received)
        End If
        txtSpeed.Text = TreeOSSource.FormatSpeed(report.Speed)
    End Sub

    Private Sub btnCancel_Click(sender As Object, e As RoutedEventArgs) Handles btnCancel.Click
        If _cts IsNot Nothing Then _cts.Cancel()
    End Sub

    ' ==================== 打开 ====================

    Private Sub btnOpenFile_Click(sender As Object, e As RoutedEventArgs) Handles btnOpenFile.Click
        If Not String.IsNullOrEmpty(_finishedPath) AndAlso File.Exists(_finishedPath) Then
            TreeOSSource.OpenUrl(_finishedPath)
        End If
    End Sub

    Private Sub btnOpenFolder_Click(sender As Object, e As RoutedEventArgs) Handles btnOpenFolder.Click
        If Not String.IsNullOrEmpty(_finishedPath) Then
            TreeOSSource.RevealInExplorer(_finishedPath)
        Else
            TreeOSSource.RevealInExplorer(TreeOSSource.GetDownloadFolder())
        End If
    End Sub

    ' ==================== 状态辅助 ====================

    Private Sub SetBusy(isBusy As Boolean)
        btnDownload.IsEnabled = Not isBusy
        btnDownload.Content = If(isBusy, "下载中…", "下载")
        btnCancel.Visibility = If(isBusy, Visibility.Visible, Visibility.Collapsed)
    End Sub

    ''' <summary>brushKey 为 Application 资源键，颜色随主题走</summary>
    Private Sub SetStatus(text As String, brushKey As String)
        txtStatus.Text = text
        Try
            Dim brush = TryCast(Application.Current.Resources(brushKey), Brush)
            If brush IsNot Nothing Then txtStatus.Foreground = brush
        Catch
        End Try
    End Sub

    Private Sub TryDelete(path As String)
        Try
            If File.Exists(path) Then File.Delete(path)
        Catch
        End Try
    End Sub

End Class
