Imports System.IO
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>
''' 商店里的一件商品卡片：商品图 + 名称 + 描述 + 价格 +「立即购买」+（可选）数字内容下载。
''' 所有内容由云端管理台下发（ShopItem），客户端不内置任何商品。
''' </summary>
Class ShopCard

    Private _item As ShopItem
    Private _ready As Boolean = False
    Private _lastImageUrl As String = ""

    Private _cts As CancellationTokenSource
    Private _isDownloading As Boolean = False
    Private _finishedPath As String = ""

    ''' <summary>没配商品图时中间显示的那个大字（一般取商品名首字）</summary>
    Public Property FallbackLetter As String = "?"

    ' ==================== 生命周期 ====================

    Private Sub Card_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        _ready = True
        Apply()
    End Sub

    ''' <summary>设置要展示的商品。还没 Loaded 就先存着，Loaded 时再刷到界面上。</summary>
    Public Sub SetItem(value As ShopItem)
        _item = value
        If _ready Then Apply()
    End Sub

    ''' <summary>当前商品（供页面判断是否需要重建卡片）</summary>
    Public ReadOnly Property Item As ShopItem
        Get
            Return _item
        End Get
    End Property

    Private Sub Apply()
        If _item Is Nothing Then Return

        txtName.Text = If(_item.Name, "").Trim()
        txtDescription.Text = If(_item.Description, "").Trim()
        txtPrice.Text = _item.PriceText
        txtFallback.Text = FallbackLetter

        Dim tag = If(_item.Tag, "").Trim()
        If tag.Length > 0 Then
            txtTag.Text = tag
            bdTag.Visibility = Visibility.Visible
        Else
            bdTag.Visibility = Visibility.Collapsed
        End If

        ' 没填购买链接就别放按钮，省得点了没反应
        btnBuy.Visibility = If(If(_item.BuyUrl, "").Trim().Length > 0,
                               Visibility.Visible, Visibility.Collapsed)

        ' 下载区：开关和地址缺一不可
        panelDownload.Visibility = If(_item.DownloadEnabled AndAlso
                                      If(_item.DownloadUrl, "").Trim().Length > 0,
                                      Visibility.Visible, Visibility.Collapsed)

        LoadImageAsync()
    End Sub

    ' ==================== 商品图 ====================

    Private Async Sub LoadImageAsync()
        Try
            Dim url = If(_item IsNot Nothing, If(_item.ImageUrl, "").Trim(), "")
            If url.Length = 0 Then Return
            ' 同一个地址只加载一次，避免页面每次轮询都重下图片
            If String.Equals(url, _lastImageUrl, StringComparison.OrdinalIgnoreCase) Then Return
            _lastImageUrl = url

            Dim img = Await TreeOSSource.GetImageAsync(New String() {url}, CacheKeyFor(url))
            If img IsNot Nothing Then
                brushImage.ImageSource = img
                hostImage.Visibility = Visibility.Visible
                txtFallback.Visibility = Visibility.Collapsed
            End If
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 由 URL 生成磁盘缓存名。别在这里做乘法哈希 —— VB 默认开整数溢出检查，
    ''' h * 31 累积几次就抛 OverflowException，图片会永远加载不出来。
    ''' </summary>
    Private Function CacheKeyFor(url As String) As String
        Dim name = url

        Dim q = name.IndexOf("?"c)
        If q >= 0 Then name = name.Substring(0, q)

        Dim slash = name.LastIndexOf("/"c)
        If slash >= 0 Then name = name.Substring(slash + 1)

        Dim sb As New System.Text.StringBuilder()
        For Each ch In name
            If Char.IsLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "-"c OrElse ch = "_"c Then
                sb.Append(ch)
            End If
        Next
        name = sb.ToString()

        If name.Length = 0 Then name = "shop-image"
        If name.Length > 48 Then name = name.Substring(name.Length - 48)
        Return name
    End Function

    ' ==================== 购买 ====================

    Private Sub btnBuy_Click(sender As Object, e As RoutedEventArgs) Handles btnBuy.Click
        If _item Is Nothing Then Return
        ' 地址可能带空格，交给 Uri 转义后再交给浏览器
        Dim target = AppUpdater.NormalizeUrl(If(_item.BuyUrl, "").Trim())
        If target.Length > 0 Then TreeOSSource.OpenUrl(target)
    End Sub

    ' ==================== 下载 ====================

    Private Async Sub btnDownload_Click(sender As Object, e As RoutedEventArgs) Handles btnDownload.Click
        If _isDownloading OrElse _item Is Nothing Then Return
        Await StartDownloadAsync()
    End Sub

    Private Sub btnCancel_Click(sender As Object, e As RoutedEventArgs) Handles btnCancel.Click
        If _cts IsNot Nothing Then _cts.Cancel()
    End Sub

    Private Sub btnOpenFolder_Click(sender As Object, e As RoutedEventArgs) Handles btnOpenFolder.Click
        If _finishedPath.Length > 0 Then
            TreeOSSource.RevealInExplorer(_finishedPath)
        Else
            TreeOSSource.RevealInExplorer(TreeOSSource.GetDownloadFolder())
        End If
    End Sub

    Private Async Function StartDownloadAsync() As Task
        Dim url = AppUpdater.NormalizeUrl(If(_item.DownloadUrl, "").Trim())
        If url.Length = 0 Then Return

        _cts = New CancellationTokenSource()
        _isDownloading = True
        btnDownload.IsEnabled = False
        btnDownload.Content = "下载中…"
        btnCancel.Visibility = Visibility.Visible
        pbDownload.Visibility = Visibility.Visible
        pbDownload.Value = 0
        txtPercent.Text = "0%"
        txtStatus.Text = "正在连接…"
        SetStatusBrush("TextSubtle")

        Dim progress As New Progress(Of DownloadReport)(AddressOf OnProgress)

        Try
            Dim folder = TreeOSSource.GetDownloadFolder()
            Dim fileName = TreeOSSource.GuessFileName(url, "download.bin")
            Dim dest = TreeOSSource.UniquePath(Path.Combine(folder, fileName))
            Dim tmp = dest & ".part"

            Await TreeOSSource.DownloadAsync(url, tmp, progress, _cts.Token)

            If File.Exists(tmp) Then
                If File.Exists(dest) Then File.Delete(dest)
                File.Move(tmp, dest)
            End If

            _finishedPath = dest
            pbDownload.Value = 100
            txtPercent.Text = "100%"
            txtStatus.Text = Path.GetFileName(dest)
            SetStatusBrush("Accent")
            btnOpenFolder.IsEnabled = True

        Catch ex As OperationCanceledException
            txtPercent.Text = ""
            txtStatus.Text = "已取消下载。"
            SetStatusBrush("TextSubtle")

        Catch ex As Exception
            txtPercent.Text = ""
            txtStatus.Text = ex.Message
            SetStatusBrush("Danger")

        Finally
            _isDownloading = False
            btnDownload.IsEnabled = True
            btnDownload.Content = "下载"
            btnCancel.Visibility = Visibility.Collapsed
            pbDownload.Visibility = Visibility.Collapsed
        End Try
    End Function

    Private Sub OnProgress(report As DownloadReport)
        If report Is Nothing Then Return
        If report.Total > 0 Then
            Dim pct = report.Received * 100.0R / report.Total
            If pct > 100.0R Then pct = 100.0R
            pbDownload.Value = pct
            txtPercent.Text = pct.ToString("0.0") & "%"
        Else
            txtPercent.Text = "--"
        End If
    End Sub

    ' ==================== 辅助 ====================

    ''' <summary>brushKey 为 Application 资源键，颜色随主题走</summary>
    Private Sub SetStatusBrush(brushKey As String)
        Try
            Dim brush = TryCast(Application.Current.Resources(brushKey), Brush)
            If brush IsNot Nothing Then txtStatus.Foreground = brush
        Catch
        End Try
    End Sub

End Class
