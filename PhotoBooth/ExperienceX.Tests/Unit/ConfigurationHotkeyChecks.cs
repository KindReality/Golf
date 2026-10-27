using System.Runtime.InteropServices;
using ExperienceX;
using PhotoBooth.Windows;
using Forms = System.Windows.Forms;

internal static class ConfigurationHotkeyChecks
{
    public static async Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                bool focused=true;int switched=0,close=0,background=0,transparency=0,names=0,brightness=0,perspectives=0,white=0,visualizations=0;
                int zoom=0, reset=0, locks=0, properties=0;
                using var keys=new ApplicationHotkeys(()=>switched++,()=>close++,()=>background++,()=>focused);
                keys.ZoomScreen=d=>zoom+=d;keys.ResetVideo=()=>reset++;
                int copied=0,pasted=0;keys.CopyScreen=()=>copied++;keys.PasteScreen=()=>pasted++;
                keys.ToggleScreenTransparency=()=>transparency++;keys.EditScreenName=()=>names++;
                keys.AdjustScreenBrightness=d=>brightness+=d;keys.ChoosePerspective=()=>perspectives++;keys.ChooseWhiteBackground=()=>white++;
                keys.AddVisualizationScreen=()=>visualizations++;
                keys.ToggleScreenLock=()=>locks++;
                keys.EditScreenProperties=()=>properties++;
                void Send(int id,uint key,uint modifiers=0)=>SendMessage(keys.Handle,0x312,id,(nint)((key<<16)|modifiers));
                Check(keys.Register(false).Length==0,"Outside configuration does not capture letters");
                Send(ApplicationHotkeys.ConfigurationId,0x43);Send(ApplicationHotkeys.RestartId,0x58);Check(switched==0&&close==0,"Queued letters outside configuration are ignored");
                Check(keys.Register(true).Length==0,"Focused configuration registers editing keys");
                Send(ApplicationHotkeys.ScreenPropertiesId,0x0d);Send(ApplicationHotkeys.ScreenPropertiesId,0x4e);
                Check(properties==1,"Enter opens screen properties; a stale or incorrect key cannot dispatch it");
                Send(ApplicationHotkeys.LockScreenId,0x4c);Check(locks==1,"L dispatches the item editing lock in configuration mode");
                Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==1,"G dispatches visualization creation while focused in configuration mode");
                Send(ApplicationHotkeys.ConfigurationId,0x45);Check(switched==0,"E no longer exits Experience");
                Send(ApplicationHotkeys.ConfigurationId,0x43);Send(ApplicationHotkeys.RestartId,0x58);Send(ApplicationHotkeys.BackgroundId,0x42);
                Send(ApplicationHotkeys.TransparencyId,0x4b);Send(ApplicationHotkeys.TransparencyId,0x54);Send(0x5846,0x46);Send(ApplicationHotkeys.NameId,0x4e);Send(ApplicationHotkeys.PerspectiveId,0x50);
                Check(switched==1&&close==1&&background==1&&transparency==1&&names==1&&perspectives==1,"K toggles chroma key; T and F are ignored; C and P remain independent");
                Send(ApplicationHotkeys.BrightnessPlusId,0xbb);Send(ApplicationHotkeys.BrightnessShiftPlusId,0xbb,4);Send(ApplicationHotkeys.BrightnessNumpadPlusId,0x6b);
                Send(ApplicationHotkeys.BrightnessMinusId,0xbd);Send(ApplicationHotkeys.BrightnessNumpadMinusId,0x6d);Check(brightness==1,"Main/numpad brightness chords remain supported");
                Send(ApplicationHotkeys.ZoomPlusId,0xbb,2);Send(ApplicationHotkeys.ZoomShiftPlusId,0xbb,6);Send(ApplicationHotkeys.ZoomNumpadPlusId,0x6b,2);
                Send(ApplicationHotkeys.ZoomMinusId,0xbd,2);Send(ApplicationHotkeys.ZoomNumpadMinusId,0x6d,2);
                Send(ApplicationHotkeys.ResetVideoId,0x52,2);Send(ApplicationHotkeys.ResetVideoId,0x52);
                Send(0x5875,0x30,2);Send(0x5876,0x60,2);
                Send(ApplicationHotkeys.ZoomPlusId,0xbb);Check(zoom==1&&reset==1&&brightness==1,"Ctrl+R resets video independently; plain R, Ctrl+0 and wrong modifiers do not dispatch it");
                Send(ApplicationHotkeys.CopyScreenId,0x43,2);Send(ApplicationHotkeys.PasteScreenId,0x56,2);
                Send(ApplicationHotkeys.CopyScreenId,0x43);Check(copied==1&&pasted==1&&switched==1,"Ctrl+C/V dispatch screen clipboard actions without switching apps or handling bare C");
                keys.Register(true,true);Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==1,"G is released while typing a screen name");Send(ApplicationHotkeys.NameId,0x4e);Send(ApplicationHotkeys.ConfigurationId,0x43);Check(names==1&&switched==1,"Typing releases editing and application-switch letters");
                Send(ApplicationHotkeys.ZoomPlusId,0xbb,2);Send(ApplicationHotkeys.ResetVideoId,0x52,2);Check(zoom==1&&reset==1,"Typing releases zoom and video reset shortcuts");
                Send(ApplicationHotkeys.CopyScreenId,0x43,2);Send(ApplicationHotkeys.PasteScreenId,0x56,2);Check(copied==1&&pasted==1,"Typing releases screen copy/paste shortcuts");
                Send(ApplicationHotkeys.ScreenPropertiesId,0x0d);Check(properties==1,"Enter is released while a dialog or text editor is active");
                keys.Register(true,submenu:ConfigurationSubmenu.Background);Send(ApplicationHotkeys.WhiteBackgroundId,0x57);Check(white==1,"W is active only for the background choice");
                int canceled=0,videos=0,mats=0,leds=0;
                keys.CancelSubmenu=()=>canceled++;keys.AddVideoScreen=()=>videos++;keys.AddMatScreen=()=>mats++;keys.AddLedScreen=()=>leds++;
                keys.Register(true,submenu:ConfigurationSubmenu.AddScreen);
                Send(ApplicationHotkeys.ScreenPropertiesId,0x0d);Check(properties==1,"The Add submenu does not open screen properties");
                Send(ApplicationHotkeys.LockScreenId,0x4c);Check(locks==1,"Add submenu does not toggle an item's lock");
                Send(ApplicationHotkeys.BackgroundId,0x42);Send(ApplicationHotkeys.NameId,0x4e);Send(ApplicationHotkeys.TransparencyId,0x4b);
                Send(ApplicationHotkeys.RestartId,0x58);Send(ApplicationHotkeys.PerspectiveId,0x50);Send(ApplicationHotkeys.ConfigurationId,0x43);
                Send(ApplicationHotkeys.CopyScreenId,0x43,2);Send(ApplicationHotkeys.ZoomPlusId,0xbb,2);Send(ApplicationHotkeys.BrightnessPlusId,0xbb);Send(ApplicationHotkeys.ResetVideoId,0x52,2);
                Check(reset==1&&background==1&&names==1&&transparency==1&&close==1&&perspectives==1&&switched==1&&copied==1&&zoom==1&&brightness==1,"Add submenu rejects every unrelated queued hotkey");
                Send(ApplicationHotkeys.VideoId,0x56);Send(ApplicationHotkeys.MatId,0x4d);Send(ApplicationHotkeys.LedId,0x4c);Send(ApplicationHotkeys.VisualizationId,0x47);
                Check(videos==1&&mats==1&&leds==1&&visualizations==2,"Add submenu accepts only its four screen types");
                Send(ApplicationHotkeys.CancelSubmenuId,0x1b);Check(canceled==1,"Esc cancels the Add submenu");
                keys.Register(true,submenu:ConfigurationSubmenu.Background);
                Send(ApplicationHotkeys.AddId,0x41);Send(ApplicationHotkeys.VideoId,0x56);Send(ApplicationHotkeys.RestartId,0x58);Send(ApplicationHotkeys.TransparencyId,0x4b);
                Check(videos==1&&close==1&&transparency==1,"Background submenu rejects unrelated screen and application actions");
                Send(ApplicationHotkeys.BackgroundId,0x42);Send(ApplicationHotkeys.WhiteBackgroundId,0x57);Send(ApplicationHotkeys.CancelSubmenuId,0x1b);
                Check(background==2&&white==2&&canceled==2,"Background submenu accepts B/W and Esc");
                keys.Register(true);Send(ApplicationHotkeys.CancelSubmenuId,0x1b);Check(canceled==2,"Queued Esc is ignored after the submenu ends");
                focused=false;keys.Register(true);Send(ApplicationHotkeys.NameId,0x4e);Check(names==1,"Another focused app receives its own letters");
                Send(ApplicationHotkeys.ScreenPropertiesId,0x0d);Check(properties==1,"An unfocused app does not capture Enter");
                Send(ApplicationHotkeys.ZoomPlusId,0xbb,2);Check(zoom==1,"Unfocused Experience releases zoom shortcuts");
                Send(ApplicationHotkeys.CopyScreenId,0x43,2);Check(copied==1,"Another focused application receives Ctrl+C normally");
                focused=true;Check(keys.Register(true).Length==0,"Editing shortcuts recover when focus returns");
                keys.Register(false);Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==2,"G is released outside configuration mode");Send(ApplicationHotkeys.NameId,0x4e);Check(names==1,"Mode exit releases queued edits");
                Send(ApplicationHotkeys.ZoomPlusId,0xbb,2);Send(ApplicationHotkeys.ResetVideoId,0x52,2);Check(zoom==1&&reset==1,"Mode exit releases zoom and video reset shortcuts");
                Send(ApplicationHotkeys.PasteScreenId,0x56,2);Check(pasted==1,"Mode exit rejects queued paste actions");
                Send(ApplicationHotkeys.ScreenPropertiesId,0x0d);Check(properties==1,"Enter is released outside configuration mode");
                done.SetResult();
            }
            catch(Exception ex){done.SetException(ex);}
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SharedShortcut();
        Console.WriteLine("PASS shared configuration toggling, focus-scoped editing, text entry, switching and ownership transfer");
    }
    private static async Task SharedShortcut()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var window=new Forms.Form{Text="Configuration shortcut check",ClientSize=new(300,90)};
            int controller=0,experience=0,step=0,focus=0; bool cm=false, em=false;
            var first=new ConfigurationShortcut("Controller",()=>cm, value=>{cm=value;controller++;},()=>focus++);
            using var second=new ConfigurationShortcut("Experience",()=>em,value=>{em=value;experience++;},()=>focus++);
            var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
            timer.Tick+=(_,_)=>
            {
                try
                {
                    if(step==0){window.Show();window.Activate();window.Focus();}
                    if(step==1){Check(first.OwnsShortcut && !second.OwnsShortcut,"Exactly one receiver owns the shared chord");SendMessage(first.Handle,0x312,0x5343,(nint)((0x43<<16)|7));Check(controller==0&&experience==0,"The former Alt chord is ignored");SendMessage(first.Handle,0x312,0x5343,(nint)((0x43<<16)|6));}
                    if(step==2){Check(controller==1&&experience==1&&cm&&em,"Hotkey enables both receivers once");second.SwitchTo("Controller");SendMessage(first.Handle,0x312,0x5343,(nint)((0x43<<16)|6));}
                    if(step==3){Check(controller==2&&experience==2&&!cm&&!em&&focus==1,"The same shortcut disables both; local focus message reaches its peer");first.Dispose();}
                    if(step==9){Check(second.OwnsShortcut,"Remaining app acquired the released chord");SendMessage(second.Handle,0x312,0x5343,(nint)((0x43<<16)|6));}
                    if(step==10){Check(controller==2&&experience==3&&em,"Remaining application takes over the global shortcut");Finish();done.SetResult();}
                    step++;
                }
                catch(Exception ex){Finish();done.SetException(ex);}
            };
            void Finish(){timer.Stop();first.Dispose();window.Close();dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);}
            timer.Start();System.Windows.Threading.Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint h,uint message,nint w,nint l);
}
