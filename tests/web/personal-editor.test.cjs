const {test}=require('node:test');
const assert=require('node:assert/strict');
const editor=require('../../src/Vndb.Web/wwwroot/personal-editor.js');
const data={title:'合成作品',novel_id:'v17',status:'played',started_on:null,finished_on:null,duration_minutes:null,score:8.5,review:'感想\n原文',notes:'备注一',extra_notes:'备注二',language:'ja'};
const record=(id='play-a',item='item-a')=>({id,item_id:item,details:{...data},revision:3,time:{base_minutes:null,session_seconds:120,adjustment_seconds:-60,total_seconds:null,pending_sessions:1}});
const fields={...data,score:'8.5',novel_id:'v17',started_on:'',finished_on:'',known:false,hours:'',minutes:''};
test('unknown and zero stay distinct; integer hours/minutes round-trip exactly',()=>{
 assert.equal(editor.details(fields,data).duration_minutes,null);
 assert.equal(editor.details({...fields,known:true,hours:'0',minutes:'0'},data).duration_minutes,0);
 assert.equal(editor.details({...fields,known:true,hours:'2',minutes:'3'},data).duration_minutes,123);
 assert.deepEqual(editor.details(fields,data),data);
});
test('invalid manual time, score, dates and status never produce a request',()=>{
 for(const patch of [{known:true,minutes:'60'},{known:true,hours:'0.5'},{known:true,hours:'9999999999'},{score:'NaN'},{score:'11'},{started_on:'2026-02-01',finished_on:'2026-01-01'},{status:'not_started',known:true,hours:'1'},{status:'unexpected'}])assert.throws(()=>editor.details({...fields,...patch},data));
});
test('unmodified blank and null notes remain equivalent to their original bytes',()=>{
 const basis={...data,notes:'',extra_notes:null,review:'  原文\n'};
 assert.equal(editor.details({...fields,notes:'',extra_notes:'',review:basis.review},basis).notes,'');
 assert.equal(editor.details({...fields,extra_notes:''},basis).extra_notes,null);
 assert.equal(editor.details({...fields,review:basis.review},basis).review,basis.review);
 const windows={...basis,review:'原文\r\n第二行\r\n'};
 assert.equal(editor.details({...fields,review:'原文\n第二行\n'},windows).review,windows.review);
});
test('uncertain reply locks one exact operation and ignores later edited input on retry',async()=>{
 let ids=0;const s=editor.session(record(),{id:'item-a'},()=>`op-${++ids}`),calls=[];
 const changed={...data,duration_minutes:123};
 await assert.rejects(s.submit(changed,async op=>{calls.push(op);throw TypeError('lost reply');}));
 assert.equal(s.state,'uncertain');assert.throws(()=>s.reload(record()));
 await s.submit({...data,review:'must not submit'},async op=>{calls.push(op);return {...record(),details:changed,revision:4};});
 assert.equal(s.state,'applied');assert.equal(ids,1);assert.deepEqual(calls[0],calls[1]);assert.equal(calls[1].body.item_id,'item-a');assert.equal(calls[1].path,'/plays/play-a');
});
test('multiple editions and replay targets never borrow another item',async()=>{
 assert.throws(()=>editor.session(record('legacy',null)),/核对历史/);
 assert.throws(()=>editor.session(null),/具体条目/);
 assert.throws(()=>editor.session(record(),{id:'item-b'}),/归属/);
 const s=editor.session(record('replay-two'),{id:'item-a'});
 await assert.rejects(s.submit(data,async()=>({...record('replay-one'),revision:4})),/不符/);assert.equal(s.state,'uncertain');
});
test('conflict retains original details and revision until explicit reload',async()=>{
 let ids=0;const s=editor.session(record(),{id:'item-a'},()=>`op-${++ids}`);
 await assert.rejects(s.submit({...data,review:'我的草稿'},async()=>{throw Object.assign(Error('conflict'),{status:409});}));
 assert.equal(s.state,'conflict');assert.equal(s.record.revision,3);
 assert.throws(()=>s.reload(record('other-play')),/原记录/);
 assert.throws(()=>s.reload(record('play-a','other-item')),/归属/);
 const latest={...record(),details:{...data,review:'Web版本'},revision:4};s.reload(latest);
 assert.equal(s.archived[0].body.details.review,'我的草稿');assert.equal(s.record.details.review,'Web版本');
 await s.submit(latest.details,async op=>{assert.equal(op.body.expected_revision,4);assert.equal(op.operationId,'op-2');return {...latest,revision:5};});
});
test('new replay creates a distinct row on the same item and retries the same new operation',async()=>{
 const s=editor.session(null,{id:'item-a'});let first;
 await assert.rejects(s.submit(data,async op=>{first=op;throw Object.assign(Error('server error'),{status:500});}));
 assert.equal(s.state,'uncertain');assert.equal(first.body.expected_revision,null);assert.equal(first.path,'/plays');
 await s.submit(data,async op=>{assert.deepEqual(op,first);return {...record('new-play'),revision:1};});
});
test('permission or validation rejection preserves draft; explicit edit retains old attempt',async()=>{
 const s=editor.session(record());await assert.rejects(s.submit(data,async()=>{throw Object.assign(Error('auth'),{status:401});}));
 assert.equal(s.state,'rejected');assert.ok(s.pending);s.reload(record());assert.equal(s.archived.length,1);assert.equal(s.pending,null);
});
test('cancelled editor model causes no write; caller cannot mutate sealed request',()=>{
 let count=0;const s=editor.session(record(),null,()=>{count++;return 'id';});const copy=s.record;copy.details.review='changed';
 assert.equal(s.record.details.review,data.review);assert.equal(count,0);assert.equal(s.pending,null);
});
test('legacy list and exact detail share one pending operation key, new replays stay item scoped',()=>{
 assert.equal(editor.editorKey({id:'play-a'},null),editor.editorKey({id:'play-a',item_id:'item-a'},{id:'item-a'}));
 assert.notEqual(editor.editorKey(null,{id:'item-a'}),editor.editorKey(null,{id:'item-b'}));
 assert.notEqual(editor.editorKey(record('play-a')),editor.editorKey(record('play-b')));
});
test('time summary separates base, session and adjustment without guessing total',()=>{
 const text=editor.time(record());assert.match(text,/手工基数：未知/);assert.match(text,/会话 120 秒/);assert.match(text,/调整 -60 秒/);assert.match(text,/合计：未知/);
});
