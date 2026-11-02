const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
class Element{
 constructor(tag='div'){this.tagName=tag;this.children=[];this.dataset={};this.hidden=false;this.value='';this.selectedOptions=[];this.classList={toggle(){}};}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=[...nodes];}setAttribute(){}addEventListener(){}querySelectorAll(){return [];}close(){this.open=false;}showModal(){this.open=true;}
}
test('actual search page returns and history recall never repeat an upstream query',async()=>{
 const elements={};for(const m of fs.readFileSync(path.join(root,'index.html'),'utf8').matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g))elements[m[2]]=new Element(m[1]);
 for(const id of ['all-nav','remote-nav']){assert.ok(fs.readFileSync(path.join(root,'shell.js'),'utf8').includes(id));elements[id]=new Element('a');}
 const calls=[],saved=new Map([['library-key','synthetic-test-key']]),preferences=new Map();
 const context={URLSearchParams,URL,AbortSignal,AbortController,setTimeout,clearTimeout,scrollY:0,requestAnimationFrame:fn=>fn(),scrollTo(){},console,location:{search:''},history:{state:null,replaceState(){},pushState(){}},
  sessionStorage:{getItem:k=>saved.get(k),setItem:(k,v)=>saved.set(k,v)},localStorage:{getItem:k=>preferences.get(k),setItem:(k,v)=>preferences.set(k,v)},
  document:{getElementById:id=>elements[id]||null,createElement:t=>new Element(t)},
  addEventListener(){},VnShell:{setView(){}},localPresence:{load:async()=>{},badge:()=>null},
  fetch:async(url,options)=>{calls.push({url,method:options.method});return {ok:true,json:async()=>url.startsWith('/api/search')?{results:[],more:false,needs_remote:options.method!=='POST'}:url.includes('producers')?[]:{novels:0,producers:0,covers:0,cover_failures:0}};},
  Option:function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;}};
 context.window=context;vm.createContext(context);
 for(const file of ['library-view.js','vn-details.js','app.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context,{filename:file});
 await context.mode('remote',{push:false,rememberOld:false});elements.query.value='Synthetic title';elements['search-form'].onsubmit({preventDefault(){}});
 for(let i=0;i<12;i++)await new Promise(r=>setImmediate(r));
 assert.equal(calls.filter(c=>c.method==='POST').length,1);
 await context.mode('remote',{push:false,rememberOld:false});assert.equal(calls.filter(c=>c.method==='POST').length,1);
 assert.match(elements.notice.textContent,/点击搜索/);
 const history=elements['search-history'].children[0],bar=history.children[2],select=bar.children[0];
 select.value='Synthetic title';elements.query.value='Other';const before=calls.length;bar.children[1].onclick();
 assert.equal(elements.query.value,'Synthetic title');assert.equal(calls.length,before);
});
