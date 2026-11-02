'use strict';
globalThis.VnDetails = (() => {
  const valid = p => p && ['japanese','original','latin'].includes(p.language) && typeof p.japanese === 'boolean';
  let preference={language:'japanese',japanese:true};
  try { const raw=VnView.storage('localStorage').get('vndb-title-preferences');if(raw&&raw.length<=1024){const saved=JSON.parse(raw);if(valid(saved))preference=saved;} } catch {}
  function title(novel,fallback,override,prefs=preference){
    const nonblank=t=>typeof t==='string'&&t.trim()?t:null,titles=Array.isArray(novel?.titles)?novel.titles:[],pick=(lang,latin=false)=>[...titles].filter(t=>t?.lang===lang).sort((a,b)=>Number(!!b.main)-Number(!!a.main)).map(t=>latin?t.latin:t.title).find(nonblank);
    const japanese=pick('ja'),original=pick(novel?.olang)||nonblank(novel?.alttitle)||nonblank(novel?.title);
    const preferred=prefs.language==='original'?original:prefs.language==='latin'?(pick(novel?.olang,true)||nonblank(novel?.title)):(japanese||original);
    const main=nonblank(override)||preferred||nonblank(fallback)||'未命名';
    return {title:main,japanese:prefs.japanese&&japanese&&japanese!==main?japanese:''};
  }
  function preferencesControl(parent,changed){
    const node=VnView.element,root=node('details','title-preferences'),bar=node('div','collection-options');root.append(node('summary','','显示标题偏好（仅此浏览器）'));
    const label=node('label','','主标题'),select=node('select');select.setAttribute('aria-label','显示标题偏好');
    for(const [value,text] of [['japanese','日文优先'],['original','作品原始语言'],['latin','罗马字／拉丁标题']]){const option=node('option','',text);option.value=value;select.append(option);}select.value=preference.language;label.append(select);
    const checkLabel=node('label','check-label'),check=node('input');check.type='checkbox';check.checked=preference.japanese;checkLabel.append(check,node('span','','显示不同的日文副标题'));
    const save=node('button','secondary','保存显示偏好');save.type='button';save.onclick=()=>{preference={language:select.value,japanese:check.checked};VnView.storage('localStorage').set('vndb-title-preferences',JSON.stringify(preference));changed();};
    bar.append(label,checkLabel,save);root.append(node('p','small','个人标题覆盖优先；不改作品原资料、目录或启动配置。'),bar);parent.append(root);
  }
  const relationLabel=code=>({preq:'前作（preq）',seq:'续作（seq）',side:'外传（side）',orig:'原作（orig）',alt:'不同版本（alt）',set:'同一背景（set）',char:'角色关联（char）',ser:'系列（ser）',fan:'同人（fan）'}[code]||'关系：'+code);
  function relations(novel,open,{links=false,known=()=>null}={}){
    const node=VnView.element,root=node('section','novel-relations'),list=node('div'),more=node('button','secondary','再显示20条关联');more.type='button';let shown=20;
    const saved=novel?.relations,invalid=saved!=null&&(!Array.isArray(saved)||saved.length>1000||saved.some(r=>!/^v[1-9][0-9]*$/.test(r?.id)||typeof r.relation!=='string'||!r.relation.trim()||r.relation.length>40||/[\u0000-\u001f\u007f]/.test(r.relation)||typeof r.official!=='boolean'));
    root.append(node('h3','','关联作品 · 只读本库'),node('p','small',invalid?'关联字段无效，原始资料保留；请显式刷新。':saved==null?'旧快照未记录关联；可显式刷新当前作品。':saved.length?'只显示已收录的直接关联，不递归补全。':'已读取关联字段，未列出关联作品。'),list,more);
    const rows=!invalid&&saved?saved:[];
    function render(){list.replaceChildren();for(const row of rows.slice(0,shown)){
      const item=node('div','related-row'),name=known(row.id),caption=name?title(name,name.title).title:'读取本库资料';
      const link=node(links?'a':'button',links?'secondary':'plain',caption+' · '+row.id);
      if(links){link.href='/?vn='+encodeURIComponent(row.id);link.target='_blank';link.rel='noopener noreferrer';}else{link.type='button';link.onclick=()=>open(row.id);}
      item.append(node('p','small',relationLabel(row.relation)+' · '+(row.official?'官方关联':'非官方关联')),link);list.append(item);
    }more.hidden=rows.length<=shown;}
    more.onclick=()=>{shown+=20;render();};render();return root;
  }
  function publicSection(novel,source,fetchedAt,developers=[],completeness='full_text'){
    const node=VnView.element,section=node('section','novel-public');section.append(node('h3','','公共资料与来源'));
    const rating=novel?.rating>0&&novel.rating<=100?(novel.rating/10).toFixed(2)+' / 10'+(Number.isInteger(novel.votecount)?' · '+novel.votecount+' 票':''):'暂无评分';
    for(const [label,value] of [['公共原文标题',title(novel,novel?.title,null,{language:'original',japanese:false}).title],['开发会社（公共资料）',developers.map(p=>p.original||p.name).join(' / ')||'未知'],['发行日期',novel?.released||'未知'],['VNDB公共评分',rating],['原始语言',novel?.olang||'未知'],['平台',(novel?.platforms||[]).join(' / ')||'未知'],['资料来源',source||'未知'],['资料观测时间',fetchedAt||'未知'],['字段覆盖',completeness==='partial_text'?'部分文字字段；简介等可能缺失':'作品文字资料；封面独立缓存']]){
      const row=node('div','detail-field');row.append(node('strong','',label),node('p','',value));section.append(row);
    }
    section.append(node('p','small','公共评分不是个人评价；发行语言/平台不能证明本机安装版本。'));return section;
  }
  return {title,preferencesControl,relations,publicSection,relationLabel};
})();
if(typeof module!=='undefined')module.exports=globalThis.VnDetails;
