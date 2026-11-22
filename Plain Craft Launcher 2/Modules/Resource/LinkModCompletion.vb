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
        Public IsPresent As Boolean
        Friend RequiredVersionId As String
        Friend ReadOnly Files As New List(Of (Project As ResourceProject, File As ResourceVersion))

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
        Dim Search = FindProjects(Item.Name, Cancellation)
        If Search.Projects.Count = 0 Then
            Item.Status = If(Search.Ambiguous, "找到多个同名模组，请在模组列表中填写项目短名",
                If(Search.HadError, "部分社区资源来源不可用，可关闭后重试", "未找到同名社区资源"))
            Return
        End If
        Item.Project = Search.Projects.OrderByDescending(Function(Project) Project.WikiId > 0).
            ThenByDescending(Function(Project) Project.LogoUrl IsNot Nothing).First()

        If Search.Projects.Count = 1 Then
            Dim Related = FindRelatedProject(Search.Projects(0), Cancellation)
            If Related IsNot Nothing Then Search.Projects.Add(Related)
        End If
        Dim HadError = LoadFiles(Item, Search.Projects, Cancellation) OrElse Search.HadError
        Dim Compatible = CompatibleFiles(Item, Version)
        Dim Installed = FindInstalled(Compatible.Select(Function(Choice) Choice.File), LocalFiles, Cancellation)
        If Installed.Local IsNot Nothing Then
            Item.IsPresent = True
            Item.Status = If(Installed.Local.IsEnabled, "已安装", "已禁用")
        ElseIf Compatible.Count = 0 Then
            Item.Status = If(HadError, "无法获取兼容文件，可关闭后重试", "没有适用于当前版本和加载器的文件")
        ElseIf FindInstalled(Item.Files.Select(Function(Choice) Choice.File), LocalFiles, Cancellation).Local IsNot Nothing Then
            Item.Status = "本地已有不兼容的版本，请先处理该文件"
        ElseIf FileUtils.Exists(Folder & GetDownloadName(Compatible(0).File)) Then
            Item.Status = "文件名与本地模组冲突"
        Else
            Item.Download = Compatible(0).File
            Item.Status = "缺失，可补全"
        End If
    End Sub

    Private Function FindProjects(Name As String, Cancellation As CancellationToken) As (Projects As List(Of ResourceProject), HadError As Boolean, Ambiguous As Boolean)
        Dim Storage As New ResourceSearcher.SearchResult
        Dim Request As New ResourceSearcher.SearchRequest(ResourceTypes.Mod, Storage, 1) With {
            .SearchText = Name, .KeepPlatformVariants = True}
        Dim Search = ResourceSearcher.GetLoader(Function() Request)
        Try
            Search.WaitForExit(Request, c:=Cancellation)
        Catch ex As Exception When ex.Message = "没有搜索结果" AndAlso Not Cancellation.IsCancellationRequested
            Return (New List(Of ResourceProject), False, False)
        Finally
            If Search.State = LoadState.Loading Then Search.Cancel()
        End Try

        '每个平台只接受原名或短名的精确匹配，避免将搜索结果中的相似项目当成预设模组。
        Dim Matches = Storage.Results.
            Where(Function(Project) String.Equals(Project.RawName, Name, StringComparison.OrdinalIgnoreCase) OrElse
                                    String.Equals(Project.Slug, Name, StringComparison.OrdinalIgnoreCase)).ToList()
        Dim Projects As New List(Of ResourceProject)
        For Each PlatformProjects In Matches.GroupBy(Function(Project) Project.Platform)
            Dim Project = PlatformProjects.FirstOrDefault(Function(Result) String.Equals(Result.Slug, Name, StringComparison.OrdinalIgnoreCase))
            If Project IsNot Nothing Then
                Projects.Add(Project)
            ElseIf PlatformProjects.IsSingle() Then
                Projects.Add(PlatformProjects.First())
            End If
        Next
        Return (Projects, Storage.ErrorMessage IsNot Nothing, Matches.Count > 0 AndAlso Projects.Count = 0)
    End Function

    Private Function LoadFiles(Item As Entry, Projects As IEnumerable(Of ResourceProject), Cancellation As CancellationToken) As Boolean
        Dim HadError As Boolean = False
        For Each Project In Projects
            Cancellation.ThrowIfCancellationRequested()
            Try
                Item.Files.AddRange(ResourceVersion.FromProjectId(Project.Id, Project.Platform, LoadDependencies:=False).
                    Where(Function(File) File.ResourceType = ResourceTypes.Mod).
                    Select(Function(File) (Project, File)))
            Catch ex As Exception When Not Cancellation.IsCancellationRequested
                Logger.Warn(ex, $"获取联机模组 {Project.RawName} 的版本列表失败")
                HadError = True
            End Try
        Next
        Return HadError
    End Function

    Private Function FindRelatedProject(Project As ResourceProject, Cancellation As CancellationToken) As ResourceProject
        Dim Platform = If(Project.Platform = ResourcePlatforms.CurseForge, ResourcePlatforms.Modrinth, ResourcePlatforms.CurseForge)
        Dim Slug = Project.GetRelatedSlug(Platform)
        If String.IsNullOrEmpty(Slug) Then Return Nothing
        Cancellation.ThrowIfCancellationRequested()
        Try
            If Platform = ResourcePlatforms.Modrinth Then Return ResourceProject.FromProjectId(Slug, Platform)
            Dim Url = "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&slug=" & StringUtils.UrlEscape(Slug)
            Dim Data = DlModRequest(Url)("data").FirstOrDefault(Function(Found) Found.Value(Of String)("slug") = Slug)
            Return If(Data Is Nothing, Nothing, New ResourceProject(DirectCast(Data, JObject)))
        Catch ex As Exception When Not Cancellation.IsCancellationRequested
            Logger.Warn(ex, $"获取 {Project.RawName} 的 {Platform} 项目失败")
            Return Nothing
        End Try
    End Function

    Private Function CompatibleFiles(Item As Entry, Version As McVersion) As List(Of (Project As ResourceProject, File As ResourceVersion))
        Return Item.Files.Where(Function(Choice) CanInstall(Choice.File, Choice.Project, Version) AndAlso
            (Item.RequiredVersionId Is Nothing OrElse
             (Choice.Project.Platform = ResourcePlatforms.Modrinth AndAlso Choice.File.Id = Item.RequiredVersionId))).
            OrderByDescending(Function(Choice) Choice.Project.Platform = ResourcePlatforms.CurseForge).
            ThenBy(Function(Choice) Choice.File.ReleaseType).ThenByDescending(Function(Choice) Choice.File.ReleaseDate).ToList()
    End Function

    Private Function CanInstall(File As ResourceVersion, Project As ResourceProject, Version As McVersion) As Boolean
        If Not File.DownloadAvailable OrElse Not If(File.RawGameVersions, File.GameVersions).
            Contains(Version.VanillaName, StringComparer.OrdinalIgnoreCase) Then Return False
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

    Private Function FindInstalled(Files As IEnumerable(Of ResourceVersion), LocalFiles As List(Of LocalResourceFile),
                                   Cancellation As CancellationToken) As (Local As LocalResourceFile, Version As ResourceVersion)
        Dim Sha1 = Files.Where(Function(File) If(File.Hash?.Length, 0) = 40).
            ToLookup(Function(File) File.Hash, StringComparer.OrdinalIgnoreCase)
        Dim Md5 = Files.Where(Function(File) If(File.Hash?.Length, 0) = 32).ToList()
        Dim WithoutHash = Files.Where(Function(File) String.IsNullOrEmpty(File.Hash) AndAlso File.Size > 0).ToList()
        If Sha1.Count + Md5.Count + WithoutHash.Count = 0 Then Return Nothing
        For Each LocalFile In LocalFiles.OrderByDescending(Function(File) File.IsEnabled)
            Cancellation.ThrowIfCancellationRequested()
            Dim Match = If(Sha1.Count > 0, Sha1(LocalFile.ModrinthHash).FirstOrDefault(), Nothing)
            If Match IsNot Nothing Then Return (LocalFile, Match)
            Dim Size = LocalFile.File.Length
            Dim PossibleMatches = Md5.Where(Function(File) File.Size <= 0 OrElse File.Size = Size).ToList()
            If PossibleMatches.Count > 0 Then
                Dim Hash = CryptographyUtils.ComputeFileHash(LocalFile.File.FullName, CryptographyUtils.HashMethod.Md5)
                Match = PossibleMatches.FirstOrDefault(Function(File) String.Equals(File.Hash, Hash, StringComparison.OrdinalIgnoreCase))
                If Match IsNot Nothing Then Return (LocalFile, Match)
            End If
            Match = WithoutHash.FirstOrDefault(Function(File) File.Size = Size AndAlso
                String.Equals(GetDownloadName(File), LocalFile.EnabledName, StringComparison.OrdinalIgnoreCase))
            If Match IsNot Nothing Then Return (LocalFile, Match)
        Next
        Return Nothing
    End Function

    Private Class InstallPlan
        Private ReadOnly Folder As String
        Private ReadOnly Version As McVersion
        Private ReadOnly LocalFiles As List(Of LocalResourceFile)
        Private ReadOnly Cancellation As CancellationToken
        Private ReadOnly Resolved As New Dictionary(Of String, ResourceVersion)(StringComparer.Ordinal)
        Private ReadOnly Dependencies As New Dictionary(Of (ResourcePlatforms, String, String), Entry)
        Private ReadOnly Targets As New Dictionary(Of String, ResourceVersion)(StringComparer.OrdinalIgnoreCase)
        Public ReadOnly Changes As New List(Of (Item As Entry, File As ResourceVersion, Existing As LocalResourceFile, Target As String, IsDependency As Boolean))

        Public Sub New(Folder As String, Version As McVersion, LocalFiles As List(Of LocalResourceFile), Cancellation As CancellationToken)
            Me.Folder = Folder
            Me.Version = Version
            Me.LocalFiles = LocalFiles
            Me.Cancellation = Cancellation
        End Sub

        Public Sub Add(Item As Entry, IsDependency As Boolean)
            Cancellation.ThrowIfCancellationRequested()
            Dim Compatible = CompatibleFiles(Item, Version)
            If Compatible.Count = 0 Then Throw New InvalidOperationException($"找不到「{Item.Name}」适用于 Minecraft {Version.VanillaName}（{Version.ModLoaders}）的文件。")
            Dim Key = If(Item.Project.WikiId > 0, $"wiki:{Item.Project.WikiId}", $"{Item.Project.Platform}:{Item.Project.Id}")
            Dim Planned As ResourceVersion = Nothing
            If Resolved.TryGetValue(Key, Planned) Then
                If Item.RequiredVersionId IsNot Nothing AndAlso Not Compatible.Any(
                    Function(Choice) Choice.File.Id = Planned.Id OrElse
                        (Not String.IsNullOrEmpty(Planned.Hash) AndAlso Choice.File.Hash = Planned.Hash)) Then
                    Throw New InvalidOperationException($"「{Item.Name}」所需的版本 {Item.RequiredVersionId} 与已选文件 {Planned.FileName} 冲突。")
                End If
                Return
            End If

            Dim Installed = FindInstalled(Compatible.Select(Function(Choice) Choice.File), LocalFiles, Cancellation)
            If Installed.Local Is Nothing AndAlso
               FindInstalled(Item.Files.Select(Function(Choice) Choice.File), LocalFiles, Cancellation).Local IsNot Nothing Then
                Throw New InvalidOperationException($"本地已有「{Item.Name}」的其他版本，请在 Mod 管理中调整后重试。")
            End If
            Dim Selected = If(Installed.Local Is Nothing, Compatible(0), Compatible.First(Function(Choice) Choice.File Is Installed.Version))
            Resolved.Add(Key, Selected.File) '先登记，避免循环依赖重复处理。
            For Each Dependency In Selected.File.RequiredDependencies
                AddDependency(Item, Selected.Project.Platform, Dependency.ProjectId, Dependency.VersionId, Dependency.FileName)
            Next
            If Installed.Local IsNot Nothing AndAlso (Installed.Local.IsEnabled OrElse Not IsDependency) Then Return

            Dim Target = If(Installed.Local?.EnabledFullName, Folder & GetDownloadName(Selected.File))
            If FileUtils.Exists(Target) Then Throw New IOException($"文件 {PathUtils.GetLastPart(Target)} 已存在，请重新检查联机模组。")
            If Targets.TryGetValue(Target, Planned) Then
                If Not String.IsNullOrEmpty(Planned.Hash) AndAlso Planned.Hash = Selected.File.Hash Then Return
                Throw New IOException($"所选模组及其前置包含同名文件 {PathUtils.GetLastPart(Target)}。")
            End If
            Targets.Add(Target, Selected.File)
            Changes.Add((Item, Selected.File, Installed.Local, Target, IsDependency))
        End Sub

        Private Sub AddDependency(Parent As Entry, Platform As ResourcePlatforms, ProjectId As String, VersionId As String, FileName As String)
            Cancellation.ThrowIfCancellationRequested()
            If String.IsNullOrEmpty(ProjectId) AndAlso String.IsNullOrEmpty(VersionId) Then
                If Not String.IsNullOrEmpty(FileName) AndAlso LocalFiles.Any(Function(File) File.IsEnabled AndAlso
                    String.Equals(File.EnabledName, FileName, StringComparison.OrdinalIgnoreCase)) Then Return
                Throw New InvalidOperationException($"「{Parent.Name}」需要前置文件「{If(FileName, "未知文件")}」，社区资源未提供可下载的项目或版本。")
            End If

            Dim Key = (Platform, ProjectId, VersionId)
            Dim Dependency As Entry = Nothing
            Try
                If Not Dependencies.TryGetValue(Key, Dependency) Then
                    Dim Exact As ResourceVersion = Nothing
                    If Platform = ResourcePlatforms.Modrinth AndAlso Not String.IsNullOrEmpty(VersionId) Then
                        Dim Data As JObject = DlModRequest("https://api.modrinth.com/v2/version/" & StringUtils.UrlEscape(VersionId))
                        Dim ActualProjectId = Data.Value(Of String)("project_id")
                        If String.IsNullOrEmpty(ActualProjectId) OrElse (Not String.IsNullOrEmpty(ProjectId) AndAlso ProjectId <> ActualProjectId) Then
                            Throw New InvalidOperationException("前置版本的项目与社区资源声明不符。")
                        End If
                        ProjectId = ActualProjectId
                        Exact = ResourceVersion.FromPlatformJson(Data, ResourceTypes.Mod)
                    End If
                    Dim Project = ResourceProject.FromProjectId(StringUtils.UrlEscape(ProjectId), Platform)
                    Dependency = New Entry(Project.RawName) With {.Project = Project, .RequiredVersionId = If(Exact Is Nothing, Nothing, VersionId)}
                    Dim Sources As New List(Of ResourceProject) From {Project}
                    If Exact Is Nothing Then
                        Dim Related = FindRelatedProject(Project, Cancellation)
                        If Related IsNot Nothing Then Sources.Add(Related)
                    Else
                        Dependency.Files.Add((Project, Exact))
                    End If
                    Dim HadError = LoadFiles(Dependency, Sources, Cancellation)
                    If HadError AndAlso Dependency.Files.Count = 0 Then Throw New InvalidOperationException("无法获取前置的文件列表。")
                    Dependencies.Add(Key, Dependency)
                End If
                Add(Dependency, True)
            Catch ex As Exception When Not Cancellation.IsCancellationRequested
                Dim DependencyName = If(Dependency?.Name, If(FileName, If(ProjectId, VersionId)))
                Throw New InvalidOperationException($"「{Parent.Name}」需要前置「{DependencyName}」。", ex)
            End Try
        End Sub
    End Class

    Public Sub Install(Selected As List(Of Entry), Folder As String, Version As McVersion,
                       Cancellation As CancellationToken, Progress As ProgressProvider)
        Dim LocalFiles = GetLocalModFiles(Folder, Version, Function() Cancellation.IsCancellationRequested, KeepDuplicates:=True)
        Dim Plan As New InstallPlan(Folder, Version, LocalFiles, Cancellation)
        For Each Item In Selected
            Plan.Add(Item, False)
        Next
        If Plan.Changes.Count = 0 Then Return

        '通过现有下载器校验文件，完成后再移动到 mods 根目录，避免覆盖同时加入的文件。
        Dim StagingFolder = Folder & ".pcl-linkmods-" & Guid.NewGuid().ToString("N") & "\"
        Dim Download As New LoaderDownload("下载联机模组",
            Plan.Changes.Where(Function(Change) Change.Existing Is Nothing).
                Select(Function(Change) Change.File.ToNetFile(StagingFolder & GetDownloadName(Change.File),
                    If(Change.IsDependency, ResourceVersion.DownloadReason.Dependency, ResourceVersion.DownloadReason.Standalone),
                    Version.VanillaName, Version.ModLoaders)).ToList())
        Try
            Cancellation.ThrowIfCancellationRequested()
            Download.WaitForExit(c:=Cancellation, p:=Progress)
            LocalFiles = GetLocalModFiles(Folder, Version, Function() Cancellation.IsCancellationRequested, KeepDuplicates:=True)
            For Each Change In Plan.Changes
                Cancellation.ThrowIfCancellationRequested()
                Dim Current = FindInstalled(CompatibleFiles(Change.Item, Version).Select(Function(Choice) Choice.File), LocalFiles, Cancellation).Local
                If Current IsNot Nothing AndAlso Current.IsEnabled Then Continue For
                If Change.Existing IsNot Nothing Then
                    File.Move(PathUtils.ForApi(Change.Existing.File.FullName), PathUtils.ForApi(Change.Target))
                    LocalFiles.RemoveAll(Function(Entry) PathUtils.IsSame(Entry.File.FullName, Change.Existing.File.FullName))
                Else
                    If FindInstalled(Change.Item.Files.Select(Function(Choice) Choice.File), LocalFiles, Cancellation).Local IsNot Nothing Then
                        Throw New IOException($"「{Change.Item.Name}」的本地文件已变更，请重新检查联机模组。")
                    End If
                    File.Move(PathUtils.ForApi(StagingFolder & GetDownloadName(Change.File)), PathUtils.ForApi(Change.Target))
                End If
                LocalFiles.Add(New LocalResourceFile(Change.Target))
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
