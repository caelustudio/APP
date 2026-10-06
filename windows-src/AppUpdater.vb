Imports System
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Diagnostics
Imports System.Windows

''' <summary>
''' 应用自更新：下载新版 exe → 直接替换自己 → 拉起新版本 → 结束旧进程。
'''
''' Windows 不允许覆盖正在运行的 exe，但**允许给它重命名**（已打开的文件句柄不受影响）。
''' 所以流程是：
'''   1. 下载到同目录的 xxx.update.exe
'''   2. 校验它确实是 Windows 可执行程序（防止下回来一张报错网页就把自己弄坏）
'''   3. 把正在运行的自己改名为 xxx.exe.bak
'''   4. 新文件改名上位，成为 xxx.exe
'''   5. 启动新版本，结束当前进程
''' 第 3、4 步任何一步失败都会把 .bak 换回去，保证不会留下一个打不开的应用。
''' 更新成功后会写一个标记文件，下一次启动用来提示「已更新到 x.x.x」并清理 .bak。
''' </summary>
Public Module AppUpdater

    ''' <summary>更新完成标记，放在 %LocalAppData%\Caelus Studio\ 下</summary>
    Private Const DoneFileName As String = "last-update.txt"

    ''' <summary>
    ''' 下载并替换自身。成功时不会返回 —— 内部会启动新版本并结束当前进程。
    ''' 失败时返回错误说明（调用方直接展示给用户）。
    ''' </summary>
    ''' <param name="url">新版本 exe 的直链</param>
    ''' <param name="version">新版本号，写进「更新完成」提示里；可以留空</param>
    ''' <param name="progress">下载字节进度</param>
    ''' <param name="status">阶段文案（准备 / 下载 / 校验 / 替换 / 重启）</param>
    ''' <param name="token">用户取消</param>
    Public Async Function InstallAsync(url As String,
                                       version As String,
                                       progress As IProgress(Of DownloadReport),
                                       status As IProgress(Of String),
                                       token As CancellationToken) As Task(Of String)

        Report(status, "正在准备…")

        ' ---- 定位当前程序文件 ----
        Dim exePath = CurrentExePath()
        If exePath.Length = 0 Then Return "无法定位当前程序文件，更新中止。"

        If String.IsNullOrWhiteSpace(url) Then
            Return "管理台没有填写更新包地址。"
        End If

        ' 管理台里可能直接填了带空格的地址（…/Caelus Studio.exe），
        ' 交给 Uri 做一次标准化，空格会被转义成 %20，否则 HttpClient 可能请求失败。
        Dim downloadUrl = NormalizeUrl(url)
        If downloadUrl.Length = 0 Then
            Return "更新包地址不是有效的网址：" & url
        End If

        Dim dir = Path.GetDirectoryName(exePath)
        Dim baseName = Path.GetFileNameWithoutExtension(exePath)
        Dim ext = Path.GetExtension(exePath)
        Dim tmpPath = Path.Combine(dir, baseName & ".update" & ext)
        Dim bakPath = exePath & ".bak"

        ' ---- 先确认程序所在目录可写（装在 Program Files 时会失败）----
        Try
            File.WriteAllText(tmpPath, "")
            File.Delete(tmpPath)
        Catch ex As Exception
            Return "程序所在目录没有写入权限：" & dir & vbCrLf &
                   "请以管理员身份重新打开本应用再更新，或把它放到有权限的目录。"
        End Try

        ' ---- 1) 下载 ----
        Report(status, "正在下载更新包…")
        Try
            Await TreeOSSource.DownloadAsync(downloadUrl, tmpPath, progress, token)
        Catch ex As OperationCanceledException
            TryDelete(tmpPath)
            Return "已取消更新。"
        Catch ex As Exception
            TryDelete(tmpPath)
            ' 网络类异常真正的线索通常在 InnerException 里
            Dim detail = ex.Message
            If ex.InnerException IsNot Nothing Then detail &= "（" & ex.InnerException.Message & "）"
            Return "下载失败：" & detail
        End Try

        If Not File.Exists(tmpPath) OrElse New FileInfo(tmpPath).Length <= 0 Then
            TryDelete(tmpPath)
            Return "下载到的文件是空的，更新中止。"
        End If

        ' ---- 2) 校验：必须是个真正的 exe，否则宁可不更新 ----
        Report(status, "正在校验安装包…")
        If Not LooksLikeExecutable(tmpPath) Then
            TryDelete(tmpPath)
            Return "下载到的不是有效的程序文件（可能下载到了一张报错页面），已放弃更新。"
        End If

        ' ---- 3) 把正在运行的自己改名（这一步 Windows 是允许的）----
        Report(status, "正在替换程序文件…")
        Try
            If File.Exists(bakPath) Then File.Delete(bakPath)
            File.Move(exePath, bakPath)
        Catch ex As Exception
            TryDelete(tmpPath)
            Return "无法重命名当前程序：" & ex.Message
        End Try

        ' ---- 4) 新版本上位 ----
        Try
            File.Move(tmpPath, exePath)
        Catch ex As Exception
            ' 换回去，绝不留一个打不开的应用
            Try
                File.Move(bakPath, exePath)
            Catch
            End Try
            Return "替换失败，已恢复原版本：" & ex.Message
        End Try

        ' ---- 5) 给下一次启动留个标记 ----
        WriteDoneFlag(version)

        ' ---- 6) 启动新版本，结束自己 ----
        Report(status, "正在启动新版本…")
        Try
            Process.Start(New ProcessStartInfo(exePath) With {
                              .UseShellExecute = True,
                              .WorkingDirectory = dir})
        Catch ex As Exception
            Return "更新已完成，但自动重启失败，请手动打开本应用：" & ex.Message
        End Try

        Try
            Application.Current.Shutdown()
        Catch
        End Try
        Environment.Exit(0)

        Return ""
    End Function

    ' ==================== 启动时的收尾 ====================

    ''' <summary>
    ''' 取走「上次更新到的版本号」并删除标记；没有就返回空串。
    ''' 只在启动调用一次，用于一次性提示「已更新到 x.x.x」。
    ''' </summary>
    Public Function TakeDoneFlag() As String
        Try
            Dim cfg = FlagPath()
            If Not File.Exists(cfg) Then Return ""
            Dim text = File.ReadAllText(cfg).Trim()
            TryDelete(cfg)
            Return text
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 清理上一次更新留下的备份与半成品。
    ''' 只有当前 exe 确实存在且看上去正常时才删 .bak —— 它是最后一道保险。
    ''' 启动后延后几秒再调用，等旧进程的句柄完全释放。
    ''' </summary>
    Public Sub CleanupLeftovers()
        Try
            Dim exePath = CurrentExePath()
            If exePath.Length = 0 OrElse Not File.Exists(exePath) Then Return

            Dim dir = Path.GetDirectoryName(exePath)
            Dim baseName = Path.GetFileNameWithoutExtension(exePath)

            ' 新版本能跑到这里，说明 .bak 已经没用了
            Dim bakPath = exePath & ".bak"
            If File.Exists(bakPath) Then
                Try
                    File.Delete(bakPath)
                Catch
                End Try
            End If

            ' 半途失败残留的 xxx.update.exe
            For Each item In Directory.GetFiles(dir, baseName & ".update*")
                Try
                    File.Delete(item)
                Catch
                End Try
            Next
        Catch
        End Try
    End Sub

    ' ==================== 内部辅助 ====================

    ''' <summary>当前 exe 的完整路径；取不到返回空串</summary>
    Public Function CurrentExePath() As String
        Try
            Dim candidate = Process.GetCurrentProcess().MainModule.FileName
            If Not String.IsNullOrWhiteSpace(candidate) AndAlso File.Exists(candidate) Then
                Return candidate
            End If
        Catch
        End Try
        Try
            Dim candidate = System.Reflection.Assembly.GetExecutingAssembly().Location
            If Not String.IsNullOrWhiteSpace(candidate) AndAlso File.Exists(candidate) Then
                Return candidate
            End If
        Catch
        End Try
        Return ""
    End Function

    ''' <summary>
    ''' 粗校验：文件头必须是 MZ，且体积不能小得离谱（排除 HTML/JSON 报错页）。
    ''' 不做完整 PE 解析，够挡住 99% 的下载事故。
    ''' </summary>
    Private Function LooksLikeExecutable(target As String) As Boolean
        Try
            Dim info As New FileInfo(target)
            If info.Length < 32768 Then Return False

            Using stream As New FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 2, False)
                If stream.Length < 32768 Then Return False
                Dim head(1) As Byte
                If stream.Read(head, 0, 2) <> 2 Then Return False
                ' 'M' & 'Z'
                Return head(0) = &H4D AndAlso head(1) = &H5A
            End Using
        Catch
            Return False
        End Try
    End Function

    ''' <summary>把地址交给 System.Uri 标准化；不是网址就返回空串</summary>
    Public Function NormalizeUrl(raw As String) As String
        Try
            Dim target As New System.Uri(raw.Trim())
            Return target.AbsoluteUri
        Catch
            Return ""
        End Try
    End Function

    Private Function FlagPath() As String
        Dim folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caelus Studio")
        Try
            If Not Directory.Exists(folder) Then Directory.CreateDirectory(folder)
        Catch
        End Try
        Return Path.Combine(folder, DoneFileName)
    End Function

    Private Sub WriteDoneFlag(version As String)
        Try
            Dim cfg = FlagPath()
            Dim text = If(version, "").Trim()
            If text.Length = 0 Then text = "新版本"
            File.WriteAllText(cfg, text)
        Catch
        End Try
    End Sub

    Private Sub TryDelete(target As String)
        Try
            If File.Exists(target) Then File.Delete(target)
        Catch
        End Try
    End Sub

    Private Sub Report(status As IProgress(Of String), message As String)
        If status Is Nothing Then Return
        Try
            status.Report(message)
        Catch
        End Try
    End Sub

End Module
