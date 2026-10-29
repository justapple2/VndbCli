const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const appearance=require('../../src/Vndb.Web/wwwroot/appearance.js');
const root=path.join(__dirname,'../../src/Vndb.Web/wwwroot');
test('appearance strips unrelated data and resolves only validated theme and motion preferences',()=>{
 assert.deepEqual(appearance.parse('{"theme":"dark","reducedMotion":true,"key":"discard","query":"discard"}'),{theme:'dark',reducedMotion:true});
 assert.deepEqual(appearance.parse('{broken'),{theme:'system',reducedMotion:false});assert.deepEqual(appearance.parse({theme:'future',reducedMotion:'false'}),{theme:'system',reducedMotion:false});
 assert.deepEqual(appearance.resolve({theme:'system'},true,true),{theme:'dark',reducedMotion:true});assert.deepEqual(appearance.resolve({theme:'light'},true,false),{theme:'light',reducedMotion:false});
});
function environment(blocked=false){
 const created=[],events={},media={},stored=[];class Node{constructor(tag){this.tag=tag;this.dataset={};this.children=[];}append(...nodes){this.children.push(...nodes);}setAttribute(k,v){this[k]=v;}addEventListener(k,fn){this[k]=fn;}focus(){this.focused=true;}}
 const bar=new Node('header'),document={documentElement:new Node('html'),readyState:'complete',querySelector:()=>bar,createElement:tag=>{const n=new Node(tag);created.push(n);return n;},createTextNode:text=>({textContent:text})};
 const env={document,addEventListener:(k,fn)=>events[k]=fn,matchMedia:q=>media[q]={matches:false,addEventListener:(_k,fn)=>media[q].changed=fn},localStorage:{getItem:()=>{if(blocked)throw Error('blocked');return '{"theme":"system","private":"discard"}';},setItem:(k,v)=>{if(blocked)throw Error('blocked');stored.push([k,v]);}}};
 return {env,created,events,media,stored,bar};
}
test('actual startup and controls persist only appearance and follow system changes',()=>{
 const s=environment();appearance.start(s.env);assert.equal(s.env.document.documentElement.dataset.theme,'light');
 s.media['(prefers-color-scheme: dark)'].matches=true;s.media['(prefers-color-scheme: dark)'].changed();assert.equal(s.env.document.documentElement.dataset.theme,'dark');
 const select=s.created.find(n=>n.tag==='select');select.value='light';select.onchange();assert.deepEqual(JSON.parse(s.stored[0][1]),{theme:'light',reducedMotion:false});
 s.media['(prefers-reduced-motion: reduce)'].matches=true;s.media['(prefers-reduced-motion: reduce)'].changed();assert.equal(s.env.document.documentElement.dataset.reducedMotion,'true');
 const menu=s.created.find(n=>n.tag==='details');menu.open=true;menu.keydown({key:'Escape',stopPropagation(){}});assert.equal(menu.open,false);assert.equal(menu.children[0].focused,true);
});
test('blocked browser storage still permits theme and reduced-motion controls',()=>{
 const s=environment(true);appearance.start(s.env);const select=s.created.find(n=>n.tag==='select');select.value='dark';select.onchange();assert.equal(s.env.document.documentElement.dataset.theme,'dark');
 const check=s.created.find(n=>n.tag==='input');check.checked=true;check.onchange();assert.equal(s.env.document.documentElement.dataset.reducedMotion,'true');
});
function luminance(hex){const c=hex.replace('#','');const channels=[0,2,4].map(i=>parseInt(c.slice(i,i+2),16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return channels[0]*.2126+channels[1]*.7152+channels[2]*.0722;}
function contrast(a,b){const x=luminance(a),y=luminance(b);return(Math.max(x,y)+.05)/(Math.min(x,y)+.05);}
test('light and dark semantic foreground and control borders meet declared contrast targets',()=>{
 const base=fs.readFileSync(path.join(root,'tokens.css'),'utf8'),css=fs.readFileSync(path.join(root,'appearance.css'),'utf8');const parse=s=>Object.fromEntries([...s.matchAll(/--([\w-]+):\s*(#[0-9a-f]{6})\b/gi)].map(m=>[m[1],m[2]]));
 const light={...parse(base),...parse(css.split(':root[data-theme')[0]),'on-primary':'#ffffff'},dark={...light,...parse(css.match(/:root\[data-theme="dark"\]\s*\{([^}]+)\}/)[1])};
 for(const [mode,p]of Object.entries({light,dark}))for(const [a,b,target]of [['ink','light',4.5],['muted','paper',4.5],['accent','light',4.5],['accent','accent-soft',4.5],['on-primary','primary',4.5],['success','success-soft',4.5],['danger','danger-soft',4.5],['input-line','light',3],['focus','paper',3]])assert.ok(contrast(p[a],p[b])>=target,`${mode} ${a}/${b}: ${contrast(p[a],p[b]).toFixed(2)}`);
});
test('every live page loads common appearance and CSS enforces system/user reduced motion',()=>{
 for(const name of ['index','manage','sessions','quality']){const html=fs.readFileSync(path.join(root,name+'.html'),'utf8');assert.match(html,/appearance.js/);assert.match(html,/appearance.css/);}
 const css=fs.readFileSync(path.join(root,'appearance.css'),'utf8');assert.match(css,/prefers-reduced-motion:reduce/);assert.match(css,/data-reduced-motion="true"/);assert.match(css,/summary:focus-visible/);assert.match(css,/\.primary:hover[^}]+transform:none!important/);
});
test('desktop theme tokens meet text, primary-button and input-boundary contrast targets',()=>{
 const xml=fs.readFileSync(path.join(__dirname,'../../src/Vndb.Desktop/Resources/Tokens.axaml'),'utf8');
 for(const name of ['Light','Dark']){const body=xml.match(new RegExp('<ResourceDictionary x:Key="'+name+'">([\\s\\S]*?)</ResourceDictionary>'))[1],p=Object.fromEntries([...body.matchAll(/x:Key="(\w+)" Color="(#[0-9a-f]{6})"/gi)].map(m=>[m[1],m[2]]));
  for(const [a,b,target]of [['TextBrush','SurfaceBrush',4.5],['MutedBrush','CanvasBrush',4.5],['AccentBrush','SurfaceBrush',4.5],['InputEdgeBrush','SurfaceBrush',3]])assert.ok(contrast(p[a],p[b])>=target,`${name} ${a}/${b}`);
  assert.ok(contrast('#ffffff',p.PrimaryBrush)>=4.5,`${name} primary button`);
 }
});
