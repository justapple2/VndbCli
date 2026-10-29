const {test}=require('node:test');
const assert=require('node:assert/strict');
const catalog=require('../../src/Vndb.Web/wwwroot/public-catalog.js');
const view=require('../../src/Vndb.Web/wwwroot/library-view.js');
function wirePage(page){return {page:1,page_size:24,source:'web_catalog',observed_at:'2026-10-29T00:00:00Z',...page,
 coverage:{processed:0,total:0,...page.coverage},items:page.items.map(i=>({...i,metadata:{vndb_id:i.metadata.novel.id,...i.metadata,novel:{title:'Synthetic',titles:[],...i.metadata.novel}},personal:{played:0,unplayed:0,unrecorded:0,needs_review:0,...i.personal}}))};}
test('public catalogue carries global filters and page independently from upstream query',()=>{
 const q=catalog.parameters({query:'日本語',language:'zh-Hans',releaseType:'complete',platform:'win',includeMtl:false,sort:'title',page:3});
 assert.equal(q.get('q'),'日本語');assert.equal(q.get('language'),'zh-Hans');assert.equal(q.get('release_type'),'complete');assert.equal(q.get('page'),'3');assert.equal(q.get('include_mtl'),'false');assert.equal(q.get('sort'),'title');
});

test('personal priority is the default request order and a validated independent browse preference',()=>{
 assert.equal(catalog.parameters({}).get('sort'),'personal');
 const state=view.store('priority-fixture',{sort:'personal',query:'',page:1});
 state.save({sort:'title'});assert.equal(state.read().sort,'title');
 state.save({sort:'personal',page:3});assert.equal(state.read().sort,'personal');assert.equal(state.read().page,3);
});
test('unrecorded and unplayed plus played replays and independent local presence are explicit text',()=>{
 assert.equal(catalog.personal({linked_items:0}),'○ 未记录 · 未加入书架');
 const p={linked_items:3,played:1,unplayed:1,unrecorded:1,needs_review:1,is_local:true};
 assert.equal(catalog.personal(p),catalog.personal({...p,is_local:false}));assert.match(catalog.personal(p),/✓ 曾玩过 1.*◷ 未玩 1.*○ 未记录 1.*! 待核对 1/);
});
test('coverage does not turn partial imports or snapshots into live website guarantees',()=>{
 const text=catalog.coverage({public_novels:10,active_state:'paused',processed:3,total:20});assert.match(text,/尚无完整导入凭证.*paused 3\/20/);assert.match(text,/不保证最新全站覆盖/);assert.match(text,/未来发行/);
 assert.match(catalog.coverage({public_novels:20,completed_snapshot_at:'2026-10-27T00:00:00Z'}),/已验证快照 2026-10-27/);
});
test('public filter state is validated and does not persist beyond session as appearance',()=>{
 const state=view.store('test',{language:'',releaseType:'',platform:'',sort:'updated',includeMtl:false});
 assert.equal(state.save({language:'zh-Hans',sort:'title'}).language,'zh-Hans');assert.equal(state.save({language:'bad code',sort:'SQL'}).language,'');assert.equal(state.read().sort,'updated');
});
test('card and list use the same compact projection without creating personal records',()=>{
 const p=catalog.adapt(wirePage({items:[{metadata:{novel:{id:'v17'},developers:[],cover:{status:'none'},fetched_at:'2026-10-28T00:00:00Z',provenance:'vndb_dump',completeness:'partial_text'},personal:{linked_items:0},releases_known:true}],coverage:{public_novels:1},total:1,more:false}));
 assert.equal(p.source,'public_catalog');assert.equal(p.results[0].cover,false);assert.equal(p.results[0].releases_known,true);assert.equal(catalog.source(p.results[0].provenance),'VNDB官方快照');
});

const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
class Node {
 constructor(){this.children=[];this.value='';this.textContent='';this.classList={toggle(){},add(){},remove(){}};this.dataset={};}
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=nodes;this.textContent='';}
 setAttribute(k,v){this[k]=v;}addEventListener(){}querySelectorAll(){return [];}
 get selectedOptions(){return [{text:this.value}];}
}
test('actual public page handlers stay read-only, filter globally, preserve errors, and do not refetch on layout changes',async()=>{
 const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot'),html=fs.readFileSync(path.join(root,'index.html'),'utf8'),nodes={};
 for(const m of html.matchAll(/\bid="([^"]+)"/g))nodes[m[1]]=new Node();for(const id of ['all-nav','remote-nav'])nodes[id]=new Node();
 let failed=false,presenceCalls=0;const calls=[],events={};
 const page=wirePage({items:Array.from({length:24},(_,n)=>({metadata:{novel:{id:'v'+(17+n),title:'Synthetic',titles:[]},developers:[],cover:{status:'none'},fetched_at:'2026-10-28T00:00:00Z',provenance:'vndb_dump',completeness:'partial_text'},personal:{linked_items:0},releases_known:true})),coverage:{public_novels:67459,active_state:'paused',processed:3,total:20},total:25,more:true});
 const context={URLSearchParams,AbortSignal,AbortController,scrollY:0,requestAnimationFrame:fn=>fn(),scrollTo(){},location:{search:'?view=local'},setTimeout,clearTimeout,history:{state:null,replaceState(){},pushState(){}},
  document:{getElementById:id=>{if(id==='detail-status')return null;assert.ok(nodes[id],id);return nodes[id];},createElement:()=>new Node()},
  addEventListener:(name,fn)=>events[name]=fn, VnShell:{setView(){}},playJournal:{hide(){}},localPresence:{load:async()=>{presenceCalls++;},badge:()=>null},
  fetch:async(url,options)=>{calls.push({url,method:options.method});if(failed)return {ok:false,status:503,json:async()=>({title:'synthetic failure'})};const index=Number(new URL(url,'https://example.test').searchParams.get('page')||1);return {ok:true,json:async()=>url.includes('/api/v1/catalog?')?index===2?{...page,page:2,items:page.items.slice(0,1),more:false}:page:url.includes('/catalog/producers?')?[]:{novels:67459,producers:100,covers:0,cover_failures:0}};}
 };
 context.Option=function(text,value){const n=new Node();n.textContent=text;n.value=value;return n;};context.window=context;
 vm.createContext(context);for(const file of ['library-view.js','public-catalog.js','app.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context);
 events.DOMContentLoaded();const drain=async()=>{for(let i=0;i<12;i++)await new Promise(resolve=>setImmediate(resolve));};await drain();
 assert.equal(nodes.content.children.length,24);assert.match(nodes['catalog-coverage'].textContent,/尚无完整导入凭证/);assert.equal(presenceCalls,0);
 nodes['public-language'].value='ja';nodes['public-language'].onchange();await drain();
 let q=new URL(calls.findLast(c=>c.url.includes('/api/v1/catalog?')).url,'https://example.test').searchParams;assert.equal(q.get('language'),'ja');assert.equal(q.get('page'),'1');
 const before=calls.length;nodes['collection-layout'].value='list';nodes['collection-layout'].onchange();await drain();assert.equal(calls.length,before);
 const old=nodes.content.children[0];failed=true;nodes.next.onclick();await drain();
 assert.equal(nodes.content.children[0],old);assert.match(nodes.notice.textContent,/synthetic failure/);const after=calls.length;await drain();assert.equal(calls.length,after);
 assert.match(nodes['page-label'].textContent,/上次成功结果.*第 1 页/);assert.equal(nodes.next.disabled,true);assert.equal(nodes.previous.disabled,true);assert.equal(nodes['retry-browse'].hidden,false);
 failed=false;nodes['retry-browse'].onclick();await drain();assert.equal(nodes.content.children.length,1);assert.equal(nodes['page-label'].textContent,'第 2 页');assert.equal(nodes['retry-browse'].hidden,true);
 assert.ok(calls.every(c=>c.method==='GET'));assert.ok(calls.every(c=>!c.url.startsWith('/api/search')));
});
test('invalid geometry and inconsistent personal counts fail before replacing cards',()=>{
 const page=wirePage({items:[],coverage:{public_novels:0},total:0,more:false});assert.equal(catalog.adapt(page).total,0);
 for(const bad of [{...page,total:1},{...page,more:true},{...page,page:2},{...page,coverage:{public_novels:0,processed:-1,total:0}}])assert.throws(()=>catalog.adapt(bad,1),SyntaxError);
 const inconsistent=wirePage({items:[{metadata:{novel:{id:'v17'},developers:[],cover:{status:'none'},fetched_at:'2026-10-28T00:00:00Z'},personal:{linked_items:1}}],coverage:{public_novels:1},total:1,more:false});
 assert.throws(()=>catalog.adapt(inconsistent),SyntaxError);
 inconsistent.items[0].personal.linked_items=0;inconsistent.items[0].metadata.novel.titles=[null];assert.throws(()=>catalog.adapt(inconsistent),SyntaxError);
});
