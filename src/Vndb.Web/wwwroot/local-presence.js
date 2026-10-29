'use strict';
window.localPresence=(() => {
  let flags=new Map(),loading=null;
  function load(){if(loading)return loading;loading=api('/api/v1/presence').then(rows=>{flags=new Map(rows.map(r=>[r.novel_id,r.is_local]));return true;}).catch(()=>{loading=null;return false;});return loading;}
  function badge(id){if(!flags.get(id))return null;const span=document.createElement('span');span.className='local-presence';span.textContent='本地已有';span.title='客户端上次同步时有安装目录或安装包；不代表可启动或文件完整。具体位置与启动配置仅保存在客户端。';return span;}
  return {load,badge};
})();
