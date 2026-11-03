const {test}=require('node:test'),assert=require('node:assert/strict');
const fields=require('../../src/Vndb.Web/wwwroot/field-comparison.js'),personal=require('../../src/Vndb.Web/wwwroot/personal-editor.js'),organization=require('../../src/Vndb.Web/wwwroot/personal-organization.js'),tools=require('../../src/Vndb.Web/wwwroot/tool-feedback.js');
const details={title:'Synthetic',novel_id:'v17',status:'played',duration_minutes:null,review:'Original',notes:'',extra_notes:null};
const record={id:'play-a',item_id:'item-a',revision:1,details};
test('three-way comparisons preserve unknown, missing, null, empty and zero values',()=>{
 const rows=fields.play(details,{...details,review:'Web'},{...details,duration_minutes:0,review:'Mine',extra_notes:''},{currentKnown:true});
 assert.equal(rows.find(r=>r.key==='review').conflict,true);assert.equal(fields.format(rows.find(r=>r.key==='duration_minutes').incoming,'duration_minutes'),'0 分钟');
 assert.equal(rows.find(r=>r.key==='extra_notes').incoming.value,'');
 const unknown=fields.play(null,null,details,{originalKnown:false});assert.ok(unknown.every(r=>!r.original.known&&!r.current.known));
 const removed=fields.play(details,null,details,{currentKnown:true});assert.ok(removed.every(r=>!r.current.exists));assert.equal(details.review,'Original');
});
test('read-only observation cannot acknowledge a lost write or change the frozen operation',async()=>{
 const session=personal.session(record,{id:'item-a'},()=> 'original-operation');let original;
 await assert.rejects(session.submit({...details,review:'Mine'},async op=>{original=op;throw Error('lost reply');}));
 session.observe({...record,revision:2,details:{...details,review:'Mine'}});
 assert.equal(session.state,'uncertain');assert.deepEqual(session.pending,original);assert.throws(()=>session.reload(record));
 assert.throws(()=>session.observe({...record,id:'other'}));assert.ok(session.review(details).some(r=>r.key==='review'));
});
test('reload retains the original comparison basis with the existing archived operation',async()=>{
 const session=personal.session(record,{id:'item-a'});await assert.rejects(session.submit({...details,review:'Mine'},async()=>{throw Object.assign(Error('conflict'),{status:409});}));
 const current={...record,revision:2,details:{...details,review:'Web'}};session.observe(current);session.reload(current);
 assert.equal(session.archived[0].basis.details.review,'Original');assert.equal(session.archived[0].body.details.review,'Mine');assert.equal(session.record.details.review,'Web');
});
test('classification review never clears a conflict and returns accurate names for exact items',async()=>{
 let current={revision:0,labels:[{id:'label-a',name:'Theme',kind:'tag'}],items:[]};
 const api=async(path,op)=>path==='/info'?{service_instance_id:'same',capabilities:['personal_organization']}:op.method==='GET'?structuredClone(current):Promise.reject(Object.assign(Error('conflict'),{status:409}));
 const writer=tools.writes(api),client=organization.create({api,writer});await client.refresh();
 await assert.rejects(client.write({items:[{item_id:'item-a',favorite:true,label_ids:['label-a']}]}));const original=writer.proof('organization');
 current={...current,revision:1,items:[{item_id:'item-b',favorite:true,label_ids:['label-a']}]};const review=await client.readReview();
 assert.equal(review.state,'conflict');assert.deepEqual(writer.proof('organization'),original);assert.equal(client.state.revision,0);assert.ok(review.rows.some(r=>r.key==='item:item-a:favorite'&&r.current.value===false));
});
test('late classification review cannot overwrite an acknowledged operation',async()=>{
 let pendingRead,readLater=false,fail=true;const base={revision:0,labels:[],items:[]};
 const api=async(path,op)=>{
  if(path==='/info')return {service_instance_id:'same',capabilities:['personal_organization']};
  if(op.method==='GET')return readLater?new Promise(r=>pendingRead=r):base;
  if(fail){fail=false;throw Error('lost');}return {revision:1,labels:[],items:op.body.change.items};
 };
 const writer=tools.writes(api),client=organization.create({api,writer});await client.refresh();const edit={items:[{item_id:'item-a',favorite:true,label_ids:[]}]};
 await assert.rejects(client.write(edit));readLater=true;const pending=client.readReview();await client.write(edit);pendingRead(base);await pending;
 assert.equal(client.state.revision,1);assert.equal(client.unsettled,false);assert.deepEqual(client.review().rows,[]);
});
