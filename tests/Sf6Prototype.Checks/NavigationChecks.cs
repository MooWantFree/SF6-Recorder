using Sf6Prototype;

public static class NavigationChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; }
        var home=new PlayerIdentity("3396336472","Me");
        var guest=new PlayerIdentity("1234567890","Opponent");
        var third=new PlayerIdentity("8888888888","Third");
        var navigation=new PlayerNavigation(); navigation.SetHome(home);
        var loaded=new List<string> {"R49","R50","R51"};
        var saved=new PlayerViewState
        {
            History=new()
            {
                Category="rank", Character="JP", OpponentCharacter="春丽", FavoritesOnly=true,Tags=["连招","防空"],MatchAllTags=true,Outcome="胜", Search="Opponent",
                Start=new(2026,11,1), End=new(2026,11,12), SelectedReplay="R51",
                Anchor=new("R50",-12.5), ScrollOffset=2534.25, VisibleReplays=loaded
            },
            Scores=new(16,true,100,"MR42",new(2026,11,1),new(2026,11,10)), PageIndex=1, DetailReplay="R49"
        };
        navigation.RememberView(saved); navigation.Visit(guest); loaded.Add("R52");
        Check(navigation.Category=="" && navigation.View.History.Anchor==null,"A new player starts with a fresh history view");
        navigation.RememberView(new() {History=new() {Category="casual",Anchor=new("C20",8),ScrollOffset=1100,SelectedReplay="C21"},Scores=new(1,false,50,"LP17")});
        navigation.Visit(third); navigation.SelectCategory("hub"); navigation.Back();
        Check(navigation.Current.Id==guest.Id && navigation.Category=="casual","Nested back navigation restores each player's own category");
        Check(navigation.View.Scores==new ScoresViewState(1,false,50,"LP17"),"Returning to an opponent restores that player's character, LP/MR, count and chart selection");
        Check(navigation.View.History is {Anchor: {ReplayId:"C20",Top:8},ScrollOffset:1100,SelectedReplay:"C21"},"Nested back navigation restores the opponent's exact anchor and selection");
        navigation.SetHome(home with {Name="Updated"},preserveView:true); navigation.Back();
        Check(navigation.IsHome && navigation.Current.Name=="Updated" && navigation.Category=="rank","Returning to the signed-in player restores ranked mode and the updated nickname");
        var restored=navigation.View.History;
        Check(restored is {OpponentCharacter:"春丽",FavoritesOnly:true,MatchAllTags:true} && restored.Tags.SequenceEqual(["连招","防空"]),"Back restores opponent and multi-tag favorite filters alongside the list viewport");
        Check(restored is {Character:"JP",Outcome:"胜",Search:"Opponent"} && restored.Start==saved.History.Start && restored.End==saved.History.End,"All filters are restored so the saved viewport represents the same matches");
        Check(restored.Anchor==saved.History.Anchor && restored.ScrollOffset==2534.25,"Back stores a replay ID plus partial-row pixel offset, not a fragile ordinal position");
        Check(restored.SelectedReplay=="R51" && navigation.View.DetailReplay=="R49","Main selection and detail-only history selection remain independent");
        Check(navigation.View.PageIndex==1 && navigation.View.Scores==saved.Scores,"Returning home restores the score page and its complete selection independently of detail-only browsing");
        Check(restored.VisibleReplays.SequenceEqual(["R49","R50","R51"]),"Rows loaded after leaving a page cannot mutate its saved list snapshot");
        navigation.SetHome(home,preserveView:true);
        Check(navigation.View.History.Anchor==saved.History.Anchor && navigation.Category=="rank","Renewing the session while at home preserves browsing position and filters");
        navigation.Visit(guest); navigation.SelectCategory("custom"); navigation.ReturnHome();
        Check(navigation.IsHome && navigation.CanGoBack && navigation.View.History.Anchor==saved.History.Anchor,"The home button restores the saved own-history position and preserves a path back");
        navigation.Visit(guest); navigation.Visit(home); navigation.SelectCategory("hub");
        navigation.SetHome(home,preserveView:true);
        Check(navigation.CanGoBack && navigation.Category=="hub","Session renewal at a revisited home page does not erase the back stack");
        navigation.Back(); Check(navigation.Current.Id==guest.Id,"A revisited home page can still go back to the previous opponent");
        navigation.SetHome(third,preserveView:true);
        Check(navigation.IsHome && navigation.Current.Id==third.Id && !navigation.CanGoBack && navigation.Category=="" && navigation.View.History.Anchor==null,"Changing accounts clears the previous account's navigation and viewport even if preserveView was requested");
        Check(navigation.View.Scores==new ScoresViewState(),"Switching accounts resets the previous player's score selection");
        Check(!navigation.CanGoForward && navigation.BackHistory.Count==0 && navigation.ForwardHistory.Count==0,"Account switches clear both directional histories");

        navigation.SetHome(home);navigation.RememberView(saved with {PageIndex=0});
        navigation.Visit(guest);
        var guestView=new PlayerViewState {History=new() {Category="casual",Anchor=new("C5",-7),ScrollOffset=270,SelectedReplay="C6",VisibleReplays=["C5","C6"]},Scores=new(1,true,50,"M8"),PageIndex=1,DetailReplay="C5"};
        navigation.RememberView(guestView);
        navigation.Visit(third);navigation.RememberView(navigation.View with {PageIndex=0});navigation.SelectCategory("custom");
        Check(navigation.BackHistory.Select(e=>e.Player.Id).SequenceEqual([guest.Id,home.Id]) && navigation.BackHistory.Select(e=>e.Steps).SequenceEqual([1,2]),"Back menus list nearest visits first with direct-jump distances");
        Check(navigation.BackHistory[1].Title=="Me - 战绩 - 收藏" && navigation.BackHistory[0].Title=="Opponent - 分数 - MR","History titles describe the saved player and actual page/filter rather than the current page");
        navigation.Back(2);
        Check(navigation.IsHome && !navigation.CanGoBack && navigation.ForwardHistory.Select(e=>e.Player.Id).SequenceEqual([guest.Id,third.Id]),"Jumping back multiple entries constructs the forward path in browser order");
        Check(navigation.ForwardHistory[1].Title=="Third - 战绩 - 比赛间","Forward menus preserve the mode from the page being left");
        navigation.Forward();
        Check(navigation.Current.Id==guest.Id && navigation.View.Scores==guestView.Scores && navigation.View.PageIndex==guestView.PageIndex && navigation.View.DetailReplay==guestView.DetailReplay && navigation.View.History.Anchor==guestView.History.Anchor && navigation.View.History.SelectedReplay==guestView.History.SelectedReplay && navigation.View.History.VisibleReplays.SequenceEqual(guestView.History.VisibleReplays),"Forward restores chart settings, history viewport, selection and detail as one saved view");
        var revised=guestView with {Scores=new(3,false,100,"LP99"),History=guestView.History with {ScrollOffset=999,Anchor=new("C19",2)}};
        navigation.RememberView(revised);navigation.Back();navigation.Forward();
        Check(navigation.View.Scores==revised.Scores && navigation.View.History.ScrollOffset==999 && navigation.View.History.Anchor==revised.History.Anchor,"Changes made after going back are captured before moving forward again");
        navigation.Forward(99);
        Check(navigation.Current.Id==third.Id && !navigation.CanGoForward,"An oversized history jump safely stops at the available end");
        navigation.Back(2);navigation.Forward(2);
        Check(navigation.Current.Id==third.Id && navigation.BackHistory.Select(e=>e.Player.Id).SequenceEqual([guest.Id,home.Id]),"Repeated multi-step traversal preserves intermediate history order");
        navigation.Back();var forwardCount=navigation.ForwardHistory.Count;
        Check(!navigation.Visit(guest) && navigation.ForwardHistory.Count==forwardCount,"Visiting the current player does not discard the forward path");
        navigation.Visit(home);
        Check(!navigation.CanGoForward,"A new visit after going back discards the old forward branch");
        navigation.ReturnHome();var backCount=navigation.BackHistory.Count;
        navigation.ReturnHome();
        Check(navigation.BackHistory.Count==backCount,"Repeated home clicks do not create duplicate navigation entries");
        navigation.Back();navigation.ReturnHome();navigation.Back();
        Check(navigation.Current.Id==guest.Id && navigation.CanGoForward,"Home remains reversible and can itself be reached by forward navigation");
        navigation.SetHome(home with {Name="最新昵称"},preserveView:true);
        Check(navigation.ForwardHistory[0].Title.StartsWith("最新昵称 - ") && navigation.CanGoForward,"Renewing the same account preserves forward history while refreshing its nickname");
        foreach(var invalidSteps in new[]{0,-1})
        {
            try {navigation.Forward(invalidSteps);throw new Exception("Expected invalid jump rejection");}
            catch(ArgumentOutOfRangeException) {count++;}
        }
        return count;
    }
}
