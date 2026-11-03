const {test}=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element{
 constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this._text='';this._value='';this.checked=false;this.open=false;this.isConnected=true;this.classList={add(){}};this.dataset={};}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=[];this._text='';this.append(...nodes);}
 set textContent(v){this._text=String(v??'');this.children=[];}get textContent(){return this._text+this.children.map(c=>c.textContent).join(' ');}
 set value(v){this._value=String(v??'');}get value(){return this._value;}
 set innerHTML(_v){throw Error('Unsafe HTML rendering');}setAttribute(k,v){this[k]=v;}removeAttribute(k){delete this[k];}addEventListener(){}focus(){}showModal(){this.open=true;}close(){this.open=false;}
}
const all=e=>[e,...e.children.flatMap(all)];
function page(route){
 const body=new Element('body'),calls=[];
 const context={URLSearchParams,AbortSignal,structuredClone,console,Option:function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;},
  document:{body,activeElement:body,createElement:t=>new Element(t),querySelector:()=>null},addEventListener(){},confirm:()=>true,VnView:{storage:()=>({get:()=>null})},
  fetch:async(url,options)=>{calls.push({url,method:options.method,body:options.body&&JSON.parse(options.body),key:options.headers['Idempotency-Key']});const r=await route(url,options);return {ok:!r.error,status:r.error||200,json:async()=>r.value??r};}};
 context.window=context;vm.createContext(context);for(const file of ['field-comparison.js','personal-editor.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context,{filename:file});
 const editor=context.VnPersonal.create();return {editor,context,body,calls,button:text=>all(body).find(e=>e.tagName==='BUTTON'&&e.textContent===text),input:name=>all(body).find(e=>e.name===name),dialog:()=>all(body).find(e=>e.tagName==='DIALOG'),form:()=>all(body).find(e=>e.tagName==='FORM')};
}
const details={title:'Synthetic',novel_id:'v17',status:'played',duration_minutes:null,score:8,review:'Original',notes:null,extra_notes:null,language:'ja',started_on:null,finished_on:null};
const original={id:'play-a',item_id:'item-a',revision:1,details};
test('actual editor displays original, Web and frozen draft while comparison performs GET only',async()=>{
 let reads=0;const p=page(async(_url,op)=>op.method!=='GET'?{error:409,value:{title:'Synthetic conflict'}}:++reads===1?original:{...original,revision:2,details:{...details,review:'Web version'}});
 await p.editor.open(original,{id:'item-a'});p.input('review').value='My frozen version';p.input('known').checked=true;p.input('hours').value='0';p.input('minutes').value='0';
 await p.form().onsubmit({preventDefault(){}});const writes=p.calls.filter(c=>c.method==='PATCH');assert.equal(writes.length,1);
 await p.button('只读当前Web对照').onclick();
 assert.match(p.body.textContent,/Original/);assert.match(p.body.textContent,/Web version/);assert.match(p.body.textContent,/My frozen version/);assert.match(p.body.textContent,/0 分钟/);
 assert.equal(p.calls.filter(c=>c.method==='PATCH').length,1);assert.equal(p.input('review').value,'My frozen version');assert.equal(p.dialog().open,true);
});
test('late comparison reply after closing cannot reopen or replace another editor',async()=>{
 let reads=0,resolve;const p=page(async()=>++reads===1?original:new Promise(r=>resolve=r));
 await p.editor.open(original,{id:'item-a'});const pending=p.button('只读当前Web对照').onclick();p.button('取消／返回').onclick();
 resolve({...original,revision:2,details:{...details,review:'Late Web'}});await pending;
 assert.equal(p.dialog().open,false);assert.doesNotMatch(p.body.textContent,/Late Web/);assert.equal(p.calls.length,2);assert.ok(p.calls.every(c=>c.method==='GET'));
});
test('long comparison values are folded text with no HTML execution',()=>{
 const p=page(async()=>original),parent=new Element('section'),text='<img src=x onerror=alert(1)>'+('x'.repeat(300));
 const rows=p.context.VnDifferences.play(details,{...details,review:'Web'},{...details,review:text},{currentKnown:true});p.context.VnDifferences.render(parent,rows);
 const expanded=all(parent).filter(e=>e.tagName==='DETAILS');assert.equal(expanded[0].open,false);assert.match(parent.textContent,/<img/);assert.equal(all(parent).some(e=>e.tagName==='IMG'),false);
});
