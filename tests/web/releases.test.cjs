const test=require('node:test'),assert=require('node:assert/strict');
const {summary,language,section}=require('../../src/Vndb.Web/wwwroot/releases.js');
test('release descriptions preserve machine translation, partial dates and bundle-specific type',()=>{
 const release={languages:[{lang:'zh-Hans',mtl:true}],platforms:['win'],official:false,patch:true,released:'2099-12',vns:[{id:'v17',rtype:'partial'},{id:'v18',rtype:'trial'}]};
 assert.match(summary(release,'v17'),/简体中文（机翻）.*非官方.*补丁.*部分.*2099-12/);
 assert.match(summary(release,'v18'),/试玩/);
 assert.equal(language('unknown-code'),'unknown-code');
});
test('release view expands bounded rows and filters cached versions without network work',async()=>{
 class Node{constructor(tag,classes,text){this.tag=tag;this.className=classes;this.textContent=text||'';this.children=[];this.value='';}append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=nodes;}setAttribute(k,v){this[k]=v;}}
 globalThis.VnView={element:(...args)=>new Node(...args),failure:e=>e.message};
 let calls=0;const items=Array.from({length:45},(_,i)=>({id:'r'+(i+1),title:'Synthetic '+i,languages:[{lang:i===44?'en':'ja'}],platforms:['win'],vns:[{id:'v17',rtype:'complete'}]}));
 const original={items,fetched_at:'2026-10-27T00:00:00Z'},novel={id:'v17',release_catalog:original};
 const root=section(novel,async()=>{calls++;return {};});
 const [,,label,content,more,refresh,status]=root.children;const input=label.children[0];
 const rows=()=>content.children.find(n=>n.tag==='ul').children;
 assert.equal(rows().length,20);assert.equal(calls,0);more.onclick();assert.equal(rows().length,40);
 input.value='英语';input.oninput();assert.equal(rows().length,1);assert.equal(more.hidden,true);assert.equal(calls,0);
 input.value='none matched';input.oninput();assert.equal(rows().length,0);assert.equal(calls,0);
 input.value='';input.oninput();assert.equal(rows().length,20);
 await refresh.onclick();assert.equal(calls,1);assert.equal(novel.release_catalog,original);assert.match(status.textContent,/原资料保留/);assert.equal(rows().length,20);
 delete globalThis.VnView;
});
