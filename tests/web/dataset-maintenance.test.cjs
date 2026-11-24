const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const source=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element{
 constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this._text='';this.hidden=false;this.disabled=false;this.open=false;}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=[];this._text='';this.append(...nodes);}
 set textContent(v){this._text=String(v??'');this.children=[];}get textContent(){return this._text+this.children.map(c=>c.textContent).join(' ');}
 set innerHTML(_){throw Error('Unsafe HTML');}setAttribute(k,v){this[k]=v;}
}
const all=e=>[e,...e.children.flatMap(all)];
const preview={id:'a'.repeat(64),sha256:'b'.repeat(64),reclaimable_bytes:1024,entries:[{id:'0',kind:'unreferenced_input',bytes:1024}]};
function page(send){
 const ids=new Map(),get=id=>{if(!ids.has(id))ids.set(id,new Element('div'));return ids.get(id);};
 const ctx={document:{getElementById:get,createElement:t=>new Element(t)},crypto:{randomUUID:()=>String(++n)},console};let n=0;
 vm.createContext(ctx);for(const file of ['tool-feedback.js','dataset-maintenance.js'])vm.runInContext(fs.readFileSync(path.join(source,file),'utf8'),ctx);
 const feature=ctx.VnDatasetMaintenance.create({api:send});
 return {feature,get,button:(id,text)=>all(get(id)).find(e=>e.tagName==='BUTTON'&&e.textContent===text)};
}
const status={available_bytes:12e9,input_bytes:1e9,stage_bytes:2e9,input_count:1,stage_count:1,completed_snapshot_at:null,wait_reason:'定时关闭'};
test('maintenance validates opaque previews and displays bounded capacity text',()=>{
 const feature=require('../../src/Vndb.Web/wwwroot/dataset-maintenance.js');assert.equal(feature.validatePreview(preview),true);
 assert.equal(feature.validatePreview({...preview,id:'../../outside'}),false);assert.equal(feature.validatePreview({...preview,entries:[{kind:'database',bytes:1}]}),false);
 assert.equal(feature.bytes(1024**3),'1.00 GiB');assert.equal(feature.bytes(-1),'未知');
});
test('cleanup lost response keeps exact request and key while refresh preserves the plan and focus controls',async()=>{
 const calls=[];let fail=true;const p=page(async(route,options)=>{calls.push({route,...structuredClone(options)});if(route.endsWith('/preview'))return structuredClone(preview);if(route.endsWith('/cleanup')){if(fail)throw TypeError('lost reply');return {preview_id:preview.id,removed:1,released_bytes:1024};}return status;});
 await p.feature.activate(true);await p.button('dataset-cleanup-preview-action','生成清理预览').onclick();
 const confirm=p.button('dataset-cleanup-actions','确认清理以上暂存');await p.feature.refresh();assert.equal(p.button('dataset-cleanup-actions','确认清理以上暂存'),confirm);
 await confirm.onclick();assert.equal(p.feature.unsettled,true);p.get('dataset-cleanup-reload').onclick();assert.match(p.get('dataset-maintenance-status').textContent,/原操作/);
 fail=false;await p.button('dataset-cleanup-actions','按原操作重试').onclick();const writes=calls.filter(c=>c.route.endsWith('/cleanup'));
 assert.deepEqual(writes[0],writes[1]);assert.equal(p.feature.unsettled,false);assert.match(p.get('dataset-cleanup-plan').textContent,/清理已确认/);
});
test('old service does not call unsupported routes and rejected approval needs explicit reread',async()=>{
 let denied=false;const calls=[];const p=page(async(route)=>{calls.push(route);if(route.endsWith('/preview'))return preview;if(route.endsWith('/cleanup')){denied=true;throw Object.assign(Error('changed'),{status:409});}return status;});
 await p.feature.activate(false);assert.deepEqual(calls,[]);assert.equal(p.get('dataset-maintenance-controls').hidden,true);
 await p.feature.activate(true);await p.button('dataset-cleanup-preview-action','生成清理预览').onclick();await p.button('dataset-cleanup-actions','确认清理以上暂存').onclick();assert.equal(denied,true);
 p.get('dataset-cleanup-reload').onclick();assert.equal(p.feature.unsettled,false);assert.ok(p.button('dataset-cleanup-preview-action','生成清理预览'));
});
