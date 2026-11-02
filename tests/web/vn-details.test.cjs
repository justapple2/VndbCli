const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
class Node{
 constructor(tag,classes,text){this.tag=tag;this.className=classes;this._text=text||'';this.children=[];this.value='';}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=nodes;this._text='';}setAttribute(k,v){this[k]=v;}
 set textContent(v){this._text=v;this.children=[];}get textContent(){return this._text+this.children.map(n=>n.textContent).join(' ');}
}
function load(saved){
 const storage=new Map(saved?[['vndb-title-preferences',saved]]:[]),context={module:{exports:{}},VnView:{element:(...args)=>new Node(...args),storage:()=>({get:k=>storage.get(k),set:(k,v)=>storage.set(k,v)}),failure:e=>e.message}};
 vm.createContext(context);for(const file of ['vn-details.js','releases.js'])vm.runInContext(fs.readFileSync(path.join(__dirname,'../../src/Vndb.Web/wwwroot',file),'utf8'),context);
 return {details:context.VnDetails,releases:context.VnReleases,storage};
}
const novel={id:'v17',title:'Latin',olang:'en',rating:84,votecount:17,titles:[{lang:'en',title:'English',latin:'English Latin',main:true},{lang:'ja',title:'合成作品',main:false}]};
test('display preferences persist locally and keep source metadata untouched',()=>{
 const {details,storage}=load('{broken'),before=JSON.stringify(novel),parent=new Node('div');let changes=0;
 assert.equal(details.title(novel).title,'合成作品');details.preferencesControl(parent,()=>changes++);
 const bar=parent.children[0].children.at(-1),select=bar.children[0].children[0],check=bar.children[1].children[0];
 select.value='latin';check.checked=true;bar.children[2].onclick();
 assert.equal(details.title(novel).title,'English Latin');assert.equal(details.title(novel).japanese,'合成作品');assert.equal(changes,1);
 assert.equal(JSON.stringify(novel),before);assert.equal(JSON.parse(storage.get('vndb-title-preferences')).language,'latin');
 assert.equal(details.title(novel,'Fallback','Personal').title,'Personal');
 assert.equal(details.title(novel,'Fallback',null,{language:'original',japanese:false}).title,'English');
});
test('direct relation navigation is bounded, exact and never prefetches names',()=>{
 const {details}=load(),clicked=[],rows=Array.from({length:45},(_,n)=>({id:'v'+(n+18),relation:n?'future-code':'seq',official:!n}));
 const root=details.relations({...novel,relations:rows},id=>clicked.push(id)),list=root.children[2],more=root.children[3];
 assert.equal(list.children.length,20);assert.equal(clicked.length,0);assert.match(list.children[0].textContent,/续作.*官方关联.*v18/);
 list.children[0].children[1].onclick();assert.deepEqual(clicked,['v18']);more.onclick();assert.equal(list.children.length,40);assert.match(list.children[1].textContent,/future-code/);
 const link=details.relations({...novel,relations:rows},null,{links:true}).children[2].children[0].children[1];
 assert.equal(link.href,'/?vn=v18');assert.equal(link.target,'_blank');assert.match(link.rel,/noopener/);
 assert.match(details.relations(novel,()=>{}).textContent,/旧快照/);assert.match(details.relations({...novel,relations:[]},()=>{}).textContent,/未列出/);
 const section=details.publicSection(novel,'vndb_dump','2026-11-02');assert.match(section.textContent,/VNDB公共评分.*8.40.*17 票/);assert.match(section.textContent,/不是个人评价/);
});
test('late release success after detail navigation cannot mutate prior metadata',async()=>{
 const {releases}=load(),original={items:[],fetched_at:'2026-11-02'},n={...novel,release_catalog:original};let active=true,resolve;
 const root=releases.section(n,()=>new Promise(r=>resolve=r),()=>active),button=root.children[5];
 const pending=button.onclick();active=false;resolve({items:[{vndb_id:'v17',novel:{release_catalog:{items:[],fetched_at:'2099-12'}}}]});await pending;
 assert.equal(n.release_catalog,original);assert.equal(button.disabled,false);
});
