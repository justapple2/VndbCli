/* Appearance only. No query, personal draft, credential or launcher state is stored here. */
(function(root,factory){const value=factory();if(typeof module==='object'&&module.exports)module.exports=value;else{root.VnAppearance=value;value.start(root);}})(globalThis,()=>{
 'use strict';const storageKey='vndb-appearance-v1';
 function parse(value){try{const p=typeof value==='string'?JSON.parse(value):value;return {theme:['system','light','dark'].includes(p?.theme)?p.theme:'system',reducedMotion:p?.reducedMotion===true};}catch{return {theme:'system',reducedMotion:false};}}
 function resolve(preference,dark,reduce){const p=parse(preference);return {theme:p.theme==='system'?(dark?'dark':'light'):p.theme,reducedMotion:p.reducedMotion||reduce};}
 function start(env){
  const doc=env.document,dark=env.matchMedia?.('(prefers-color-scheme: dark)'),reduce=env.matchMedia?.('(prefers-reduced-motion: reduce)');
  let preference;try{preference=parse(env.localStorage.getItem(storageKey));}catch{preference=parse(null);}
  let select,checkbox;
  function apply(){const current=resolve(preference,!!dark?.matches,!!reduce?.matches);doc.documentElement.dataset.theme=current.theme;doc.documentElement.dataset.reducedMotion=String(current.reducedMotion);if(select)select.value=preference.theme;if(checkbox)checkbox.checked=preference.reducedMotion;}
  function save(){try{env.localStorage.setItem(storageKey,JSON.stringify(preference));}catch{}apply();}
  apply();dark?.addEventListener?.('change',apply);reduce?.addEventListener?.('change',apply);
  env.addEventListener('storage',e=>{if(e.key===storageKey){preference=parse(e.newValue);apply();}});
  function controls(){
   const bar=doc.querySelector('.topbar');if(!bar)return;
   const menu=doc.createElement('details');menu.className='appearance-menu';const summary=doc.createElement('summary');summary.textContent='外观';summary.setAttribute('aria-label','明暗主题与减少动画');
   const panel=doc.createElement('div');panel.className='appearance-controls';const label=doc.createElement('label');label.textContent='主题';select=doc.createElement('select');select.setAttribute('aria-label','主题');
   for(const [value,text]of [['system','跟随系统'],['light','浅色'],['dark','深色']]){const option=doc.createElement('option');option.value=value;option.textContent=text;select.append(option);}label.append(select);
   const motion=doc.createElement('label');motion.className='check-label';checkbox=doc.createElement('input');checkbox.type='checkbox';checkbox.setAttribute('aria-label','减少动画');motion.append(checkbox,doc.createTextNode('减少动画'));
   const note=doc.createElement('p');note.className='small';note.textContent='只保存外观偏好。系统要求减少动画时始终生效。';panel.append(label,motion,note);menu.append(summary,panel);bar.append(menu);
   select.onchange=()=>{preference={...preference,theme:select.value};save();};checkbox.onchange=()=>{preference={...preference,reducedMotion:checkbox.checked};save();};
   menu.addEventListener('keydown',e=>{if(e.key==='Escape'){menu.open=false;summary.focus();e.stopPropagation();}});apply();
  }
  if(doc.readyState==='loading')doc.addEventListener('DOMContentLoaded',controls,{once:true});else controls();
 }
 return Object.freeze({parse,resolve,start,storageKey});
});
