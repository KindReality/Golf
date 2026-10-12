const hooked=new Set();
const stats={d3d9Presents:0,fullscreenPresents:0,fullscreenPresented:0,fullscreenErrors:0,glSwaps:0,devices:[],resets:[],windows:[],displayChanges:[],graphicsModules:Process.enumerateModules().map(m=>m.name).filter(n=>/g-force|d3d|opengl|dxgi/i.test(n))};
const contexts=new Map();
function attachOnce(address,handlers){const key=address.toString();if(hooked.has(key))return;hooked.add(key);Interceptor.attach(address,handlers);}
function parameters(p){const offset=Process.pointerSize===8?40:32;return {width:p.readU32(),height:p.add(4).readU32(),format:p.add(8).readU32(),swapEffect:p.add(24).readU32(),window:p.add(Process.pointerSize===8?32:28).readPointer().toString(),windowed:p.add(offset).readU32()!==0,refresh:p.add(offset+16).readU32(),interval:p.add(offset+20).readU32()};}
function release(p){new NativeFunction(p.readPointer().add(2*Process.pointerSize).readPointer(),'uint',['pointer'])(p);}
function device(p,info){if(p.isNull())return;contexts.set(p.toString(),info);const table=p.readPointer();attachOnce(table.add(17*Process.pointerSize).readPointer(),{onEnter(args){stats.d3d9Presents++;this.fullscreen=contexts.get(args[0].toString())?.windowed===false;if(this.fullscreen)stats.fullscreenPresents++;},onLeave(result){if(this.fullscreen){if(result.toInt32()===0)stats.fullscreenPresented++;else{stats.fullscreenErrors++;if(stats.fullscreenErrors<5)send({kind:'fullscreen-present-failed',result:result.toInt32()});}}}});attachOnce(table.add(16*Process.pointerSize).readPointer(),{onEnter(args){this.handle=args[0].toString();this.params=parameters(args[1]);},onLeave(result){const event={...this.params,result:result.toInt32()};if(result.toInt32()===0)contexts.set(this.handle,this.params);stats.resets.push(event);send({kind:'d3d9-reset',...event});}});}
const user=Process.getModuleByName('user32.dll');
const d3d=Process.findModuleByName('d3d9.dll');
if(d3d){
 const create=new NativeFunction(d3d.getExportByName('Direct3DCreate9'),'pointer',['uint']);const direct=create(32);const table=direct.readPointer();
 attachOnce(table.add(16*Process.pointerSize).readPointer(),{onEnter(args){this.params={...parameters(args[5]),adapter:args[1].toInt32(),deviceType:args[2].toInt32(),flags:args[4].toInt32()};this.output=args[6];},onLeave(result){const event={...this.params,result:result.toInt32()};stats.devices.push(event);send({kind:'d3d9-create-device',...event});if(result.toInt32()>=0)device(this.output.readPointer(),this.params);}});
 const createWindow=new NativeFunction(user.getExportByName('CreateWindowExW'),'pointer',['uint','pointer','pointer','uint','int','int','int','int','pointer','pointer','pointer','pointer']);
 const hidden=createWindow(0x08000000,Memory.allocUtf16String('STATIC'),Memory.allocUtf16String('GForce graphics probe'),0x80000000,0,0,16,16,ptr(0),ptr(0),ptr(0),ptr(0));
 const params=Memory.alloc(Process.pointerSize===8?64:56);params.writeByteArray(new Uint8Array(Process.pointerSize===8?64:56));params.writeU32(16);params.add(4).writeU32(16);params.add(12).writeU32(1);params.add(24).writeU32(1);params.add(Process.pointerSize===8?32:28).writePointer(hidden);params.add(Process.pointerSize===8?40:32).writeU32(1);
 const output=Memory.alloc(Process.pointerSize);const createDevice=new NativeFunction(table.add(16*Process.pointerSize).readPointer(),'int',['pointer','uint','uint','pointer','uint','pointer','pointer']);
 const result=createDevice(direct,0,1,hidden,0x44,params,output);send({kind:'probe-entry-points',result});if(result>=0)release(output.readPointer());release(direct);new NativeFunction(user.getExportByName('DestroyWindow'),'bool',['pointer'])(hidden);
}
const gl=Process.findModuleByName('opengl32.dll');if(gl){const gdi=Process.getModuleByName('gdi32.dll');attachOnce(gdi.getExportByName('SwapBuffers'),{onEnter(){stats.glSwaps++;}});}
function str(p,wide=false){if(p.isNull())return '';if(p.toUInt32()<65536)return '#'+p.toUInt32();try{return wide?p.readUtf16String():p.readUtf8String();}catch(_){return '<unreadable>';}}
for(const suffix of ['A','W']){
 attachOnce(user.getExportByName('CreateWindowEx'+suffix),{onEnter(args){this.event={class:str(args[1],suffix==='W'),title:str(args[2],suffix==='W'),style:args[3].toUInt32(),exstyle:args[0].toUInt32(),x:args[4].toInt32(),y:args[5].toInt32(),width:args[6].toInt32(),height:args[7].toInt32()};},onLeave(result){if(this.event.class.includes('SSFullscreen')){const event={...this.event,handle:result.toString()};stats.windows.push(event);send({kind:'window-created',...event});}}});
 attachOnce(user.getExportByName('ChangeDisplaySettingsEx'+suffix),{onEnter(args){const event={device:str(args[0],suffix==='W'),flags:args[3].toUInt32(),hasMode:!args[1].isNull()};stats.displayChanges.push(event);send({kind:'display-change',...event});}});
}
setInterval(()=>send({kind:'summary',...stats}),200);
send({kind:'graphics-trace-ready',pid:Process.id,modules:stats.graphicsModules});
