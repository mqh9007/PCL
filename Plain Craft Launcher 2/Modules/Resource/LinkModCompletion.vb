Public Module LinkModCompletion

    Public Function ParseNames(Value As String) As List(Of String)
        '同时兼容旧版保存的换行列表。
        Return Value.Split({","c, ChrW(13), ChrW(10)}, StringSplitOptions.RemoveEmptyEntries).
            Select(Function(Name) Name.Trim()).Where(Function(Name) Name.Length > 0).
            Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Public Class Entry
        Public ReadOnly Name As String
        Public Project As ResourceProject
        Public Download As ResourceVersion
        Public Status As String
        Friend ReadOnly KnownFiles As New List(Of ResourceVersion)

        Public Sub New(Name As String)
            Me.Name = Name
        End Sub
    End Class

    Public Function Inspect(Names As IEnumerable(Of String), Folder As String, Version As McVersion,
                            Cancellation As CancellationToken) As List(Of Entry)
        Dim LocalFiles = GetLocalModFiles(Folder, Version, Function() Cancellation.IsCancellationRequested, KeepDuplicates:=True)
        Dim Entries As New List(Of Entry)
        For Each Name In Names
            Cancellation.ThrowIfCancellationRequested()
            Dim Item As New Entry(Name)
            Entries.Add(Item)
            Try
                InspectEntry(Item, Folder, Version, LocalFiles, Cancellation)
            Catch ex As Exception When Not Cancellation.IsCancellationRequested
                Logger.Warn(ex, $"检查联机模组 {Name} 失败")
                Item.Status = "检查失败，可关闭后重试"
            End Try
        Next
        Cancellation.ThrowIfCancellationRequested()
        Return Entries
    End Function

    Private Sub InspectEntry(Item As Entry, Folder As String, Version As McVersion, LocalFiles As List(Of LocalResourceFile),
                             Cancellation As CancellationToken)
        Dim Storage As New ResourceSearcher.SearchResult
        Dim Request As New ResourceSearcher.SearchRequest(ResourceTypes.Mod, Storage, 1) With {
            .SearchText = Item.Name, .KeepPlatformVariants = True}
        Dim Search = ResourceSearcher.GetLoader(Function() Request)
        Try
            Search.WaitForExit(Request, c:=Cancellation)
        Catch ex As Exception When ex.Message = "没有搜索结果" AndAlso Not Cancellation.IsCancellationRequested
            Item.Status = "未找到同名社区资源"
            Return
        Finally
            If Search.State = LoadState.Loading Then Search.Cancel()
        End Try

        '每个平台只接受原名或短名的精确匹配，避免将搜索结果中的相似项目当成预设模组。
        Dim Matches = Storage.Results.
            Where(Function(Project) String.Equals(Project.RawName, Item.Name, StringComparison.OrdinalIgnoreCase) OrElse
                                    String.Equals(Project.Slug, Item.Name, StringComparison.OrdinalIgnoreCase)).ToList()
        Dim Projects As New List(Of ResourceProject)
        For Each PlatformProjects In Matches.GroupBy(Function(Project) Project.Platform)
            Dim Project = PlatformProjects.FirstOrDefault(Function(Result) String.Equals(Result.Slug, Item.Name, StringComparison.OrdinalIgnoreCase))
            If Project IsNot Nothing Then
                Projects.Add(Project)
            ElseIf PlatformProjects.IsSingle() Then
                Projects.Add(PlatformProjects.First())
            End If
        Next
        If Projects.Count = 0 Then
            If Matches.Count > 0 Then
                Item.Status = "找到多个同名模组，请在模组列表中填写项目短名"
            Else
                Item.Status = If(Storage.ErrorMessage Is Nothing, "未找到同名社区资源", "部分社区资源来源不可用，可关闭后重试")
            End If
            Return
        End If
        Item.Project = Projects.OrderByDescending(Function(Project) Project.WikiId > 0).
            ThenByDescending(Function(Project) Project.LogoUrl IsNot Nothing).First()

        Dim Compatible As New List(Of ResourceVersion)
        Dim HadError = Storage.ErrorMessage IsNot Nothing
        For Each Project In Projects
            Cancellation.ThrowIfCancellationRequested()
            Try
                Dim Files = ResourceVersion.FromProjectId(Project.Id, Project.Platform, LoadDependencies:=False).
                    Where(Function(File) File.ResourceType = ResourceTypes.Mod).ToList()
                Item.KnownFiles.AddRange(Files)
                Compatible.AddRange(Files.Where(Function(File) CanInstall(File, Project, Version)))
            Catch ex As Exception When Not Cancellation.IsCancellationRequested
                Logger.Warn(ex, $"获取联机模组 {Project.RawName} 的版本列表失败")
                HadError = True
            End Try
        Next

        Dim Installed = FindInstalled(Item, LocalFiles, Cancellation)
        If Installed IsNot Nothing Then
            Item.Status = If(Installed.IsEnabled, "已安装", "已禁用")
            Return
        End If
        If Compatible.Count = 0 Then
            Item.Status = If(HadError, "无法获取兼容文件，可关闭后重试", "没有适用于当前版本和加载器的文件")
            Return
        End If
        Dim Download = Compatible.OrderBy(Function(File) File.ReleaseType).ThenByDescending(Function(File) File.ReleaseDate).First()
        If FileUtils.Exists(Folder & GetDownloadName(Download)) Then
            Item.Status = "文件名与本地模组冲突"
            Return
        End If
        Item.Download = Download
        Item.Status = "缺失，可补全"
    End Sub

    Private Function CanInstall(File As ResourceVersion, Project As ResourceProject, Version As McVersion) As Boolean
        If Not If(File.RawGameVersions, File.GameVersions).Contains(Version.VanillaName, StringComparer.OrdinalIgnoreCase) Then Return False
        Dim Loaders = File.ModLoaders
        If Loaders = ModLoaders.None AndAlso Project.ModLoaders.Flags.IsSingle() Then Loaders = Project.ModLoaders
        If Loaders = ModLoaders.None Then
            If Version.Drop >= 140 OrElse Not Version.ModLoaders.HasFlagF(ModLoaders.Forge) Then Return False
        ElseIf (Loaders And Version.ModLoaders) = ModLoaders.None Then
            Return False
        End If
        Return File.DownloadUrls.Any() AndAlso File.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) = -1 AndAlso
            {"jar", "zip", "litemod"}.Contains(PathUtils.GetExtension(File.FileName))
    End Function

    Private Function GetDownloadName(File As ResourceVersion) As String
        Return File.FileName.Replace("~", "-")
    End Function

    Private Function FindInstalled(Item As Entry, LocalFiles As List(Of LocalResourceFile),
                                   Cancellation As CancellationToken) As LocalResourceFile
        If Item.KnownFiles.Count = 0 Then Return Nothing
        Dim Sha1 As New HashSet(Of String)(Item.KnownFiles.Where(Function(File) If(File.Hash?.Length, 0) = 40).Select(Function(File) File.Hash),
                                            StringComparer.OrdinalIgnoreCase)
        Dim Md5 = Item.KnownFiles.Where(Function(File) If(File.Hash?.Length, 0) = 32).ToList()
        Dim WithoutHash = Item.KnownFiles.Where(Function(File) String.IsNullOrEmpty(File.Hash) AndAlso File.Size > 0).ToList()
        For Each LocalFile In LocalFiles.OrderByDescending(Function(File) File.IsEnabled)
            Cancellation.ThrowIfCancellationRequested()
            If Sha1.Count > 0 AndAlso Sha1.Contains(LocalFile.ModrinthHash) Then Return LocalFile
            Dim Size = LocalFile.File.Length
            Dim PossibleMatches = Md5.Where(Function(File) File.Size <= 0 OrElse File.Size = Size).ToList()
            If PossibleMatches.Count > 0 Then
                Dim Hash = CryptographyUtils.ComputeFileHash(LocalFile.File.FullName, CryptographyUtils.HashMethod.Md5)
                If PossibleMatches.Any(Function(File) String.Equals(File.Hash, Hash, StringComparison.OrdinalIgnoreCase)) Then Return LocalFile
            End If
            If WithoutHash.Any(Function(File) File.Size = Size AndAlso
                               String.Equals(GetDownloadName(File), LocalFile.EnabledName, StringComparison.OrdinalIgnoreCase)) Then Return LocalFile
        Next
        Return Nothing
    End Function

    Public Sub Install(Selected As List(Of Entry), Folder As String, Version As McVersion,
                       Cancellation As CancellationToken, Progress As ProgressProvider)
        Dim LocalFiles = GetLocalModFiles(Folder, Version, Function() Cancellation.IsCancellationRequested, KeepDuplicates:=True)
        Dim Files As New Dictionary(Of String, Entry)(StringComparer.OrdinalIgnoreCase)
        Dim Hashes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Item In Selected
            Cancellation.ThrowIfCancellationRequested()
            If FindInstalled(Item, LocalFiles, Cancellation) IsNot Nothing Then Continue For
            Dim File = Item.Download
            Dim Name = GetDownloadName(File)
            If FileUtils.Exists(Folder & Name) Then Throw New IOException($"文件 {Name} 已存在，请重新检查联机模组。")
            If Not String.IsNullOrEmpty(File.Hash) AndAlso Not Hashes.Add(File.Hash) Then Continue For
            If Files.ContainsKey(Name) Then Throw New IOException($"所选模组包含同名文件 {Name}。")
            Files.Add(Name, Item)
        Next
        If Files.Count = 0 Then Return

        '通过现有下载器校验文件，完成后再移动到 mods 根目录，避免覆盖同时加入的文件。
        Dim StagingFolder = Folder & ".pcl-linkmods-" & Guid.NewGuid().ToString("N") & "\"
        Dim Download As New LoaderDownload("下载联机模组",
            Files.Select(Function(Pair) Pair.Value.Download.ToNetFile(StagingFolder & Pair.Key, ResourceVersion.DownloadReason.Standalone, Version.VanillaName, Version.ModLoaders)).ToList())
        Try
            Cancellation.ThrowIfCancellationRequested()
            Download.WaitForExit(c:=Cancellation, p:=Progress)
            LocalFiles = GetLocalModFiles(Folder, Version, Function() Cancellation.IsCancellationRequested, KeepDuplicates:=True)
            For Each Pair In Files
                Cancellation.ThrowIfCancellationRequested()
                If FindInstalled(Pair.Value, LocalFiles, Cancellation) IsNot Nothing Then Continue For
                Dim Target = Folder & Pair.Key
                File.Move(PathUtils.ForApi(StagingFolder & Pair.Key), PathUtils.ForApi(Target))
                LocalFiles.Add(New LocalResourceFile(Target))
            Next
        Finally
            If Download.State = LoadState.Loading Then Download.Cancel()
            Try
                If DirectoryUtils.Exists(StagingFolder) Then DirectoryUtils.Delete(StagingFolder)
            Catch ex As Exception
                Logger.Warn(ex, "清理联机模组下载目录失败")
            End Try
        End Try
    End Sub

End Module
