const {test} = require('node:test');
const assert = require('node:assert/strict');
const view = require('../../src/Vndb.Web/wwwroot/library-view.js');
const defaults = {query:'', page:1, layout:'grid', density:'comfortable', fit:'contain', selected:'', scroll:0};
test('unrecorded, explicit unplayed, replay progress and local presence remain independent',()=>{
  assert.equal(view.progressLabel(null),'状态未读取');
  assert.equal(view.progressLabel({progress:{state:'unrecorded',statuses:[]}}),'未记录');
  assert.equal(view.progressLabel({progress:{state:'unplayed',statuses:['not_started']}}),'未玩');
  assert.equal(view.progressLabel({is_local:false,progress:{state:'played',statuses:['not_started','completed']}}),'未玩 / 已完成');
  assert.equal(view.progressLabel({progress:{needs_review:true,statuses:['completed']}}),'关联待核对');
});
function environment() {
  const values = new Map(), stack = [{state:null, url:'/?view=local'}]; let position = 0;
  return {sessionStorage:{getItem:k=>values.get(k), setItem:(k,v)=>values.set(k,v)}, history:{
    get state(){return stack[position].state;},
    pushState(state, _, url){stack.splice(++position); stack.push({state:structuredClone(state),url});},
    replaceState(state, _, url){stack[position]={state:structuredClone(state),url};},
    back(){position--;}
  }};
}
test('each view retains its own query, selection and position across back navigation', () => {
  const env=environment(), local=view.store('local',defaults,env), plays=view.store('plays',defaults,env);
  local.save({query:'日本語の作品',page:3,selected:'v17',scroll:720});
  plays.save({query:'別の作品',page:2},{push:true,url:'/?view=plays'});
  local.save({query:'次の検索',page:1},{push:true,url:'/?view=local'});
  env.history.back(); assert.equal(plays.read().query,'別の作品');
  env.history.back(); assert.deepEqual(local.read(),{...defaults,query:'日本語の作品',page:3,selected:'v17',scroll:720});
});
test('invalid or unrelated stored fields never become UI preferences or credentials', () => {
  const env=environment(); env.sessionStorage.setItem('vndb-view-local',JSON.stringify({page:-1,layout:'bad',query:[],key:'synthetic',path:'synthetic',scroll:Infinity}));
  const state=view.store('local',defaults,env); assert.deepEqual(state.read(),defaults);
  assert.deepEqual(state.save({page:2,layout:'list',key:'synthetic'}),{...defaults,page:2,layout:'list'});
});
test('blocked storage and history still allow a working in-memory view', () => {
  const env={get sessionStorage(){throw Error('blocked');},get history(){throw Error('blocked');}};
  const state=view.store('local',defaults,env); state.save({query:'作品',layout:'list'});
  assert.equal(state.read().query,'作品'); assert.equal(state.read().layout,'list');
});
test('latest reader cancels superseded work and rejects late responses', async () => {
  const read=view.latest(), old=read.begin(), current=read.begin();
  assert.equal(old.signal.aborted,true); assert.equal(old.current(),false); assert.equal(current.current(),true);
  read.cancel(); assert.equal(current.current(),false); assert.equal(current.signal.aborted,true);
});
test('only appearance survives a new browser session; searches and item selection do not', () => {
  const preferences=new Map(), env=environment();env.localStorage={getItem:k=>preferences.get(k),setItem:(k,v)=>preferences.set(k,v)};
  view.store('local',defaults,env).save({query:'合成の個人検索',page:3,selected:'test-item-1',scroll:500,layout:'list'});
  assert.equal([...preferences.values()].some(text=>text.includes('個人検索')||text.includes('test-item-1')),false);
  const fresh=environment();fresh.localStorage=env.localStorage;
  assert.deepEqual(view.store('local',defaults,fresh).read(),{...defaults,layout:'list'});
});
test('errors retain distinct permission, conflict, size and transport explanations', () => {
  assert.match(view.failure({status:401}),/密钥/);assert.match(view.failure({status:409}),/核对/);
  assert.match(view.failure({status:413}),/限额/);assert.match(view.failure({status:429}),/频繁/);
  assert.match(view.failure(new TypeError('network')),/保留/);assert.equal(view.failure(Error('协议不兼容')),'协议不兼容');
});
