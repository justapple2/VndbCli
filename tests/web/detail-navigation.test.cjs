const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element{
 constructor(tag='div'){this.tag=tag;this.children=[];this.dataset={};this.value='';this.selectedOptions=[];this.events={};this.classList={toggle(){}};this._text='';}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=nodes;this._text='';}setAttribute(k,v){this[k]=v;}
 set textContent(v){this._text=String(v??'');this.children=[];}get textContent(){return this._text+this.children.map(n=>n.textContent).join(' ');}
 addEventListener(name,fn){this.events[name]=fn;}querySelectorAll(){return [];}close(){if(this.open){this.open=false;this.events.close?.();}}showModal(){this.open=true;}
}
const descendants=e=>[e,...e.children.flatMap(descendants)];
const entry=id=>({novel:{id,title:'Synthetic '+id,titles:[],languages:[],platforms:[],relations:[],release_catalog:{items:[],fetched_at:'2026-11-02T00:00:00Z'}},producers:[],provenance:'vndb_dump'});
function page(route){
 const elements={},calls=[];for(const m of fs.readFileSync(path.join(root,'index.html'),'utf8').matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g))elements[m[2]]=new Element(m[1]);
 for(const id of ['all-nav','remote-nav'])elements[id]=new Element('a');
 const context={URLSearchParams,URL,AbortSignal,AbortController,setTimeout,clearTimeout,scrollY:0,requestAnimationFrame:fn=>fn(),scrollTo(){},location:{search:''},history:{state:null,replaceState(){},pushState(){}},
  sessionStorage:{getItem:()=>null,setItem(){}},localStorage:{getItem:()=>null,setItem(){}},document:{getElementById:id=>elements[id]||Object.values(elements).flatMap(descendants).find(e=>e.id===id)||null,createElement:t=>new Element(t)},
  addEventListener(){},VnShell:{setView(){}},localPresence:{load:async()=>{},badge:()=>null},playJournal:{hide(){},forNovel(){}},
  fetch:async(url,options)=>{calls.push({url,method:options.method});const value=await route(url,options);return {ok:true,json:async()=>value};},
  Option:function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;}};
 context.window=context;vm.createContext(context);for(const file of ['library-view.js','vn-details.js','public-catalog.js','releases.js','app.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context,{filename:file});
 return {context,elements,calls,button:label=>descendants(elements['detail-content']).find(e=>e.tag==='button'&&e.textContent===label)};
}
test('changing page invalidates a delayed detail even if transport ignores cancellation',async()=>{
 let resolve;const p=page(()=>new Promise(r=>resolve=r)),pending=p.context.detail('v17');
 await p.context.mode('remote',{read:false});resolve(entry('v17'));await pending;
 assert.notEqual(p.elements['detail-dialog'].open,true);assert.equal(p.calls.length,1);assert.equal(p.calls[0].method,'GET');
});
test('returning to the same novel cannot revive a release action from the detached old view',async()=>{
 const parent=entry('v17'),related=entry('v18'),original=parent.novel.release_catalog;let resolve;
 const p=page(url=>url.includes('/releases/')?new Promise(r=>resolve=r):url.endsWith('v17')?parent:related);
 await p.context.detail('v17');const pending=p.button('刷新发行目录').onclick();
 await p.context.detail('v18',true);p.button('返回原详情').onclick();
 resolve({items:[{vndb_id:'v17',novel:{id:'v17',release_catalog:{items:[],fetched_at:'2099-12-01T00:00:00Z'}}}]});await pending;
 assert.equal(parent.novel.release_catalog,original);assert.equal(p.calls.filter(c=>c.url.startsWith('/api/library')).length,2);
});
test('release reply with a mismatched inner novel ID preserves original data',async()=>{
 const parent=entry('v17'),original=parent.novel.release_catalog;
 const p=page(url=>url.includes('/releases/')?{items:[{vndb_id:'v17',novel:{id:'v18',release_catalog:{items:[],fetched_at:'2099-12-01T00:00:00Z'}}}]}:parent);
 await p.context.detail('v17');await p.button('刷新发行目录').onclick();
 assert.equal(parent.novel.release_catalog,original);assert.match(p.elements['detail-content'].textContent,/无效|身份不符/);
});
