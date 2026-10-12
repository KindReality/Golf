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
                bool focused=true;int exit=0,close=0,background=0,transparency=0,sizing=0,names=0,brightness=0,perspectives=0,white=0,visualizations=0;
                using var keys=new ApplicationHotkeys(()=>exit++,()=>close++,()=>background++,()=>focused);
                keys.ToggleScreenTransparency=()=>transparency++;keys.ToggleScreenSizing=()=>sizing++;keys.EditScreenName=()=>names++;
                keys.AdjustScreenBrightness=d=>brightness+=d;keys.ChoosePerspective=()=>perspectives++;keys.ChooseWhiteBackground=()=>white++;
                keys.AddVisualizationScreen=()=>visualizations++;
                void Send(int id,uint key,uint modifiers=0)=>SendMessage(keys.Handle,0x312,id,(nint)((key<<16)|modifiers));
                Check(keys.Register(false).Length==0,"Outside configuration does not capture letters");
                Send(ApplicationHotkeys.ConfigurationId,0x45);Send(ApplicationHotkeys.CloseId,0x58);Check(exit==0&&close==0,"Queued letters outside configuration are ignored");
                Check(keys.Register(true).Length==0,"Focused configuration registers editing keys");
                Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==1,"G dispatches visualization creation while focused in configuration mode");
                Send(ApplicationHotkeys.ConfigurationId,0x43);Check(exit==0,"C no longer exits Experience");
                Send(ApplicationHotkeys.ConfigurationId,0x45);Send(ApplicationHotkeys.CloseId,0x58);Send(ApplicationHotkeys.BackgroundId,0x42);
                Send(ApplicationHotkeys.TransparencyId,0x54);Send(ApplicationHotkeys.SizingId,0x53);Send(ApplicationHotkeys.NameId,0x4e);Send(ApplicationHotkeys.PerspectiveId,0x50);
                Check(exit==1&&close==1&&background==1&&transparency==1&&sizing==1&&names==1&&perspectives==1,"E and existing editing keys dispatch independently");
                Send(ApplicationHotkeys.BrightnessPlusId,0xbb);Send(ApplicationHotkeys.BrightnessShiftPlusId,0xbb,4);Send(ApplicationHotkeys.BrightnessNumpadPlusId,0x6b);
                Send(ApplicationHotkeys.BrightnessMinusId,0xbd);Send(ApplicationHotkeys.BrightnessNumpadMinusId,0x6d);Check(brightness==1,"Main/numpad brightness chords remain supported");
                keys.Register(true,true);Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==1,"G is released while typing a screen name");Send(ApplicationHotkeys.NameId,0x4e);Send(ApplicationHotkeys.ConfigurationId,0x45);Check(names==1&&exit==1,"Typing releases editing and exit letters");
                keys.Register(true,choosingBackground:true);Send(ApplicationHotkeys.WhiteBackgroundId,0x57);Check(white==1,"W is active only for the background choice");
                focused=false;keys.Register(true);Send(ApplicationHotkeys.NameId,0x4e);Check(names==1,"Another focused app receives its own letters");
                focused=true;Check(keys.Register(true).Length==0,"Editing shortcuts recover when focus returns");
                keys.Register(false);Send(ApplicationHotkeys.VisualizationId,0x47);Check(visualizations==1,"G is released outside configuration mode");Send(ApplicationHotkeys.NameId,0x4e);Check(names==1,"Mode exit releases queued edits");
                done.SetResult();
            }
            catch(Exception ex){done.SetException(ex);}
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SharedShortcut();
        Console.WriteLine("PASS shared configuration entry, focus-scoped editing, text entry, exit and ownership transfer");
    }
    private static async Task SharedShortcut()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var window=new Forms.Form{Text="Configuration shortcut check",ClientSize=new(300,90)};
            int controller=0,experience=0,step=0;
            var first=new ConfigurationShortcut("Controller",()=>controller++);
            using var second=new ConfigurationShortcut("Experience",()=>experience++);
            var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
            timer.Tick+=(_,_)=>
            {
                try
                {
                    if(step==0){window.Show();window.Activate();window.Focus();}
                    if(step==1){Check(first.OwnsShortcut && !second.OwnsShortcut,"Exactly one receiver owns the shared chord");SendMessage(first.Handle,0x312,0x5343,(nint)((0x43<<16)|7));}
                    if(step==2){Check(controller==1&&experience==1,"Hotkey Windows message enables both receivers once");first.Dispose();}
                    if(step==9){Check(second.OwnsShortcut,"Remaining app acquired the released chord");SendMessage(second.Handle,0x312,0x5343,(nint)((0x43<<16)|7));}
                    if(step==10){Check(controller==1&&experience==2,"Remaining application takes over the global shortcut");Finish();done.SetResult();}
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
