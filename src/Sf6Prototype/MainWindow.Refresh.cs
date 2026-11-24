using System.Windows.Threading;

namespace Sf6Prototype;

public partial class MainWindow
{
    private Task SynchronizeAsync()
    {
        ClosePlayerSearch();
        return BeginLoadAsync(PlayerLoadIntent.Refresh);
    }

    private Task BeginLoadAsync(PlayerLoadIntent intent)
    {
        if (_busy || _preparingViews > 0 || !_ready || _exitRequested || _stoppingRefresh) return Task.CompletedTask;

        var page = PageTabs.SelectedIndex == 1 ? RefreshPage.Scores :
            History.FavoritesOnly ? RefreshPage.Favorites : RefreshPage.History;
        var request = new PlayerRefreshRequest(intent, page, History.Category, Scores.Range);
        var pager = Pager;
        var route = request.Route(pager, History.Filtered);
        if (route == RefreshRoute.ReloadLocal) ReloadLocal(preserveViewport:true);
        if (route is RefreshRoute.Skip or RefreshRoute.ReloadLocal) return Task.CompletedTask;

        History.AllowPageUpdates();
        var operation = request.Reset ? SyncOperation.ManualRefresh : SyncOperation.LoadHistory;
        return _sync.RunAsync(new(operation, Quiet:!request.Reset && page != RefreshPage.Scores),
            context => LoadPagesAsync(request, pager, route, context));
    }

    private RefreshViewTarget? CurrentRefreshView() =>
        _exitRequested || ViewedHistoryOwner != _navigation.Current.Id ? null : new(ViewedHistoryOwner, _navigationVersion);

    private void ReloadRefreshView(RefreshViewTarget target, CancellationToken token, bool automatic = false, bool refreshImages = true)
    {
        if (target.CanPublish(CurrentRefreshView(), token))
            ReloadLocal(preserveViewport:true, refreshImages:refreshImages, automaticRefresh:automatic);
    }

    private async Task PublishRefreshPageAsync(RefreshViewTarget target, CancellationToken token, bool automatic = false)
    {
        await target.PublishPageAsync(CurrentRefreshView, _archive.PrepareAsync,
            () => ReloadLocal(preserveViewport:true, refreshImages:false, automaticRefresh:automatic), token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private async Task LoadPagesAsync(PlayerRefreshRequest request, BattlePager pager, RefreshRoute route, SyncContext context)
    {
        HideNotice();
        var token = context.Token;
        var target = new RefreshViewTarget(pager.Owner, _navigationVersion);
        var completed = false;
        if (request.Reset) pager.Reset(request.Category);

        void BindSignedInAccount(SavedSession session, bool preserveView)
        {
            SetActiveAccount(session, preserveView);
            pager = Pager;
            target = target with { Owner = pager.Owner };
            if (!preserveView) request = request with { Range = Scores.Range };
            if (request.Reset) pager.Reset(request.Category);
        }

        try
        {
            if (route == RefreshRoute.CachedPage)
            {
                await pager.LoadMoreAsync(null, request.Category, History.Filtered, token, () => PublishRefreshPageAsync(target, token));
                completed = true;
                context.Complete();
                return;
            }

            var session = _vault.Load();
            if (session == null)
            {
                await StopBackgroundDownloadsAsync();
                token.ThrowIfCancellationRequested();
                SetStatus("login_needed", Strings.CompleteCapcomSignIn);
                ProgressStage(Strings.WaitingForSignIn);
                session = await LoginWindow.SignInAsync(this, _settings, _vault);
                token.ThrowIfCancellationRequested();
                if (session == null) { SetStatus("login_cancelled", Strings.SignInCancelled); return; }
                BindSignedInAccount(session, preserveView:session.AccountId == _settings.PlayerId);
            }
            token.ThrowIfCancellationRequested();

            var refreshAt = DateTimeOffset.UtcNow;
            DailyProfileRefresh? ProfileForPlayer() => request.Intent == PlayerLoadIntent.More ? null : new(_profiles, pager.Owner, refreshAt);
            var daily = ProfileForPlayer();
            using var client = CreateClient(session, observeCharacterNames:daily?.Required == true);
            async Task<RefreshPlan> PreparePlan()
            {
                if (client.AccountId != _settings.PlayerId && _vault.Load() is { } validated)
                {
                    await StopBackgroundDownloadsAsync();
                    token.ThrowIfCancellationRequested();
                    BindSignedInAccount(validated, preserveView:false);
                    daily = ProfileForPlayer();
                }
                client.CharacterObserver = daily?.Required == true ? ObserveCharacterNames : null;
                RefreshAccountUi();
                return request.CreatePlan(pager, _settings.PlayerId, History.Filtered, daily);
            }

            var workflow = await RefreshWorkflow.RunAsync(client, daily?.Required == true, PreparePlan, new()
            {
                Request=context.Report, Progress=(progress,metadata)=>context.Report(progress,metadata),
                BeforeValidation=()=> { ProgressStage(Strings.CheckingSession); SetStatus("validating",Strings.CheckingSessionProgress); },
                PrepareImages=()=>PrepareImageDownloadsAsync(token), ImagesReady=StartImageDownloads, AvatarUpdated=RefreshAccountUi,
                PageSaved=()=>PublishRefreshPageAsync(target, token), PreferredCategory=()=>PreferredHistoryCategory(pager.Owner),
                BattlesLoaded=()=>ReloadRefreshView(target, token, refreshImages:false),
                BeforeProfile=duringPaging=>
                {
                    SetStatus("profiles",Strings.UpdatingProfilesProgress);
                    if (_progressVisible)
                    {
                        if (duringPaging) _refreshProgress.SetStage(Strings.UpdatingCharacterProfiles);
                        else _refreshProgress.Metadata(1);
                        UpdateButtonProgress();
                    }
                }
            }, automatic:false, token);

            _lastSync = workflow.Sync;
            RecordRefreshResult(pager.Owner, _lastSync.Inserted);
            ReloadRefreshView(target, token);
            RefreshAccountUi();
            SetStatus(workflow.ScoresPending ? "sync_complete_scores_pending" : "sync_complete",
                Localization.Format(Strings.RefreshCompleteSummary, _lastSync.Inserted, _lastSync.Pages, _lastSync.Requests, ""));
            completed = true;
            context.Complete();
            if (pager.Owner == _settings.PlayerId) _autoRefresh.Resume(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!context.SilentCancellation) SetStatus("cancelled", Strings.CancelledLoadedSaved);
        }
        catch (Exception ex)
        {
            context.Fail(ex);
            ReloadRefreshView(target, token);
            ReportError(ex);
        }
        finally
        {
            if (request.Reset && pager.Owner == _settings.PlayerId) _autoRefresh.Restart(DateTimeOffset.UtcNow);
            if (target.CanPublish(CurrentRefreshView(), token))
            {
                if (completed && !_closeRequested) History.ContinueAutoLoad();
                else History.StopAutoLoad();
            }
            if (App.Capture) { await Task.Delay(200); Capture(); }
        }
    }

    private Task<bool> AutoRefreshAccountAsync(SavedSession session) =>
        _sync.RunAsync(new(SyncOperation.AutomaticRefresh,Quiet:true,Background:true,SilentCancellation:true), context=>AutoRefreshAccountCoreAsync(session,context));
    private async Task<bool> AutoRefreshAccountCoreAsync(SavedSession session,SyncContext context)
    {
        var token=context.Token;
        var owner=session.AccountId;
        var daily=new DailyProfileRefresh(_profiles,owner,DateTimeOffset.UtcNow);
        var pager=new BattlePager(_store,owner);
        var target = new RefreshViewTarget(owner, _navigationVersion);
        var completed=false;
        try
        {
            using var client=CreateClient(session,owner,observeCharacterNames:daily.Required);
            var workflow=await RefreshWorkflow.RunAsync(client,daily.Required,()=>
            {
                if(client.AccountId!=owner) throw new LoginRequiredException(Strings.AccountChanged);
                _pagers[owner]=pager;
                return Task.FromResult(new RefreshPlan(pager,RefreshMode.Account,Profile:daily));
            },new()
            {
                Request=context.Report,Progress=(progress,metadata)=>context.Report(progress,metadata),
                PrepareImages=()=>PrepareImageDownloadsAsync(token),ImagesReady=StartImageDownloads,AvatarUpdated=RefreshAccountUi,
                PageSaved=()=>PublishRefreshPageAsync(target, token, automatic:true),
                PreferredCategory=()=>PreferredHistoryCategory(owner),
                BattlesLoaded=()=>ReloadRefreshView(target, token, automatic:true, refreshImages:false),
                BeforeProfile=duringPaging=>
                {
                    if(duringPaging) _refreshProgress.SetStage(Strings.UpdatingCharacterProfiles);
                    else _refreshProgress.Metadata(1);
                    UpdateButtonProgress();
                }
            },automatic:true,token);
            ReloadRefreshView(target, token);
            RefreshAccountUi();
            RecordRefreshResult(owner,workflow.Sync.Inserted);
            if(workflow.ScoresPending) ShowNotice(Strings.RecordsUpdatedScoresPending,true);
            completed=true;context.Complete();
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if(ex is LoginRequiredException) _autoRefresh.Pause();
            context.Fail(ex);
            ReportError(ex);
        }
        finally
        {
            _autoRefresh.Restart(DateTimeOffset.UtcNow);
            History.ContinueAutoLoad();
        }
        return completed;
    }
}
