import sys, os, time, json, ctypes, argparse, subprocess
from pathlib import Path
root=Path(__file__).resolve().parent;workspace=root.parents[1];sys.path.insert(0,str(workspace/'.tools/gforce-python'))
import frida
parser=argparse.ArgumentParser();parser.add_argument('--run',required=True);args=parser.parse_args()
run=Path(args.run);run.mkdir(parents=True,exist_ok=True)
device=frida.get_local_device();owned=None;session=None;script=None;child=None
exe=r'C:\Program Files (x86)\SoundSpectrum\G-Force\G-Force Standalone.x64.exe'
prefs=Path(os.environ['APPDATA'])/'SoundSpectrum/G-Force/Preferences (Standalone).txt';original=prefs.read_bytes()
(run/'standalone-preferences.backup').write_bytes(original)
if any(p.name.startswith('G-Force Standalone') for p in device.enumerate_processes()):raise RuntimeError('An independently launched standalone is running. It will not be modified.')
log=(run/'graphics.jsonl').open('w',encoding='utf8')
def message(m,data):
    value=m.get('payload',m)
    if value.get('kind')=='summary':
        replacement=run/'graphics-summary.tmp';replacement.write_text(json.dumps(value,indent=2),encoding='utf8');replacement.replace(run/'graphics-summary.json')
    else:log.write(json.dumps(value)+'\n');log.flush();print(json.dumps(value),flush=True)
try:
    prefs.write_bytes(original.replace(b'G-Force.AutoLaunchToolbar = 1',b'G-Force.AutoLaunchToolbar = 0'))
    child=subprocess.Popen([exe],cwd=str(Path(exe).parent));owned=child.pid
    time.sleep(4);session=device.attach(owned)
    script=session.create_script((root/'Trace-GForceGraphics.js').read_text(encoding='utf8'));script.on('message',message);script.load()
    (run/'process.json').write_text(json.dumps({'pid':owned,'executable':exe}),encoding='utf8')
    expires=time.monotonic()+90
    while not (run/'stop').exists() and time.monotonic()<expires:
        time.sleep(.2)
finally:
    if script:
        try:script.unload()
        except frida.InvalidOperationError:pass
    if session:
        try:session.detach()
        except frida.InvalidOperationError:pass
    if owned:
        user=ctypes.WinDLL('user32');callback=ctypes.WINFUNCTYPE(ctypes.c_bool,ctypes.c_void_p,ctypes.c_void_p)
        user.GetWindowThreadProcessId.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_ulong)];user.PostMessageW.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_size_t,ctypes.c_ssize_t]
        def close(h,l):
            pid=ctypes.c_ulong();user.GetWindowThreadProcessId(h,ctypes.byref(pid))
            if pid.value==owned:user.PostMessageW(h,0x10,0,0)
            return True
        user.EnumWindows(callback(close),None);time.sleep(1)
        if any(p.pid==owned for p in device.enumerate_processes()):device.kill(owned)
    prefs.write_bytes(original);log.close();print('Owned G-Force stopped; original preferences restored.',flush=True)
