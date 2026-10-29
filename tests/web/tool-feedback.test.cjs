const {test}=require('node:test');
const assert=require('node:assert/strict');
const tools=require('../../src/Vndb.Web/wwwroot/tool-feedback.js');

test('tool feedback distinguishes permission/conflict/limits/protocol and unknown states',()=>{
 for(const [e,text]of [[{status:401},'鉴权'],[{status:409},'冲突'],[{status:413},'限额'],[{status:400,code:'import_item_limit'},'限额'],[{status:400,code:'import_kind_mismatch'},'格式'],[{status:429},'受限'],[{code:'protocol_invalid'},'证明'],[{},'保留']])assert.match(tools.failure(e),new RegExp(text));
 assert.equal(tools.label('dependency_pending'),'依赖待补全');assert.equal(tools.label('needs_review'),'候选待核对');assert.match(tools.label('future'),/future/);
});
test('unknown writes retain exact path/body/key despite caller mutation; opposite action cannot replace them',async()=>{
 const calls=[];let fail=true,ids=0;const writes=tools.writes(async(path,op)=>{calls.push({path,...structuredClone(op)});if(fail)throw TypeError('lost');return {id:'same'};},()=>`op-${++ids}`);
 const body={expected_revision:3,details:{review:'原稿'}};
 await assert.rejects(writes.execute('candidate','/accept',body));body.details.review='修改';
 await assert.rejects(writes.execute('candidate','/reject',body),/原操作/);assert.equal(calls.length,1);assert.equal(writes.peek('candidate').state,'uncertain');
 writes.reload();assert.equal(writes.peek('candidate').id,'op-1');fail=false;await writes.retry('candidate');
 assert.deepEqual(calls[0],calls[1]);assert.equal(ids,1);assert.equal(writes.unsettled,false);
});
test('conflict and clear rejection require explicit reread and preserve original evidence',async()=>{
 for(const status of [400,401,409,413]){
  let calls=0;const writes=tools.writes(async()=>{calls++;throw Object.assign(Error('rejected'),{status});},()=>`op-${calls}`);
  await assert.rejects(writes.execute('x','/x',{revision:7}));await assert.rejects(writes.retry('x'));assert.equal(calls,1);
  assert.equal(writes.proof('x').body.revision,7);writes.reload();assert.equal(writes.peek('x'),null);assert.equal(writes.archived[0].json,'{"revision":7}');
 }
});
test('malformed success remains uncertain; valid completion does not repeat after refresh failure',async()=>{
 let valid=false,calls=0;const writes=tools.writes(async()=>{calls++;return valid?{id:'right'}:{id:'wrong'};},()=> 'original');
 await assert.rejects(writes.execute('x','/x',null,r=>r.id==='right'));assert.equal(writes.peek('x').state,'uncertain');
 valid=true;await writes.retry('x');assert.equal(calls,2);assert.equal(writes.peek('x'),null);
});
test('latest read guards invalidate both late success and late failure without a new write',()=>{
 const reads=tools.latest(),old=reads.next(),fresh=reads.next();assert.equal(old(),false);assert.equal(fresh(),true);reads.cancel();assert.equal(fresh(),false);
});
test('rate limiting keeps the original retryable operation without claiming an unknown result',async()=>{
 let limited=true;const calls=[],writes=tools.writes(async(path,options)=>{calls.push({path,...options});if(limited)throw Object.assign(Error('rate'),{status:429});return {ok:true};},()=> 'fixed-id');
 await assert.rejects(writes.execute('x','/x',{revision:1}));assert.equal(writes.peek('x').state,'retryable');writes.reload();limited=false;await writes.retry('x');assert.deepEqual(calls[0],calls[1]);
});
