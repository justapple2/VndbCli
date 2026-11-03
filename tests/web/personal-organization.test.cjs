const {test}=require('node:test'),assert=require('node:assert/strict');
const organization=require('../../src/Vndb.Web/wwwroot/personal-organization.js');
const tools=require('../../src/Vndb.Web/wwwroot/tool-feedback.js');
const initial=()=>({revision:0,labels:[],items:[]});
test('personal classification replies validate exact items, revisions and empty removals',()=>{
 const body={expected_revision:0,items:[{item_id:'item-a',favorite:true,label_ids:[]}]};
 assert.equal(organization.confirms({revision:1,labels:[],items:body.items},body),true);
 assert.equal(organization.confirms({revision:1,labels:[],items:[{item_id:'item-b',favorite:true,label_ids:[]}]},body),false);
 assert.equal(organization.confirms({revision:2,labels:[],items:body.items},body),false);
 assert.equal(organization.confirms({revision:1,labels:[],items:[]},{expected_revision:0,items:[{item_id:'item-a',favorite:false,label_ids:[]}]}),true);
 assert.throws(()=>organization.validate({revision:0,labels:[],items:[{item_id:'item-a',favorite:true,label_ids:['unknown']}]}));
});
test('lost classification reply uses the same operation and cannot silently replace its payload',async()=>{
 let current=initial(),fail=true;const sent=[];
 const api=async(path,options)=>{
  if(path==='/info')return {service_instance_id:'synthetic-instance',capabilities:['personal_organization']};
  if(options.method==='GET')return structuredClone(current);
  sent.push(structuredClone(options));const body=options.body.change;current={revision:1,labels:[],items:body.items};
  if(fail){fail=false;throw Error('Synthetic response lost');}return structuredClone(current);
 };
 const writer=tools.writes(api),client=organization.create({api,writer});await client.refresh();
 await assert.rejects(client.write({items:[{item_id:'item-a',favorite:true,label_ids:[]}]}));assert.equal(client.unsettled,true);
 await assert.rejects(client.write({items:[{item_id:'item-b',favorite:true,label_ids:[]}]}));assert.equal(sent.length,1);
 await client.refresh();assert.equal(client.state.revision,0); // An uncertain operation blocks rereading over the original.
 const saved=await writer.retry('organization');assert.equal(saved.revision,1);assert.equal(sent[0].operationId,sent[1].operationId);assert.deepEqual(sent[0].body,sent[1].body);
 assert.equal(sent[0].body.service_instance_id,'synthetic-instance');assert.equal(client.unsettled,false);
});
test('classification conflict requires explicit reread and preserves the original request',async()=>{
 let current=initial();const api=async(path,options)=>{
  if(path==='/info')return {service_instance_id:'synthetic-instance',capabilities:['personal_organization']};
  if(options.method==='GET')return current;
  throw Object.assign(Error('Synthetic conflict'),{status:409});
 };
 const writer=tools.writes(api),client=organization.create({api,writer});await client.refresh();
 await assert.rejects(client.write({label:{id:'a',name:'想玩',kind:'collection'}}));
 current={revision:1,labels:[{id:'b',name:'Other',kind:'tag'}],items:[]};await client.refresh();assert.equal(client.state.revision,0);
 await client.refresh(true);assert.equal(client.state.revision,1);assert.equal(writer.archived.length,1);assert.equal(writer.archived[0].state,'conflict');
});
test('classification refresh does not adopt another service instance',async()=>{
 let instance='first';const api=async path=>path==='/info'?{service_instance_id:instance,capabilities:['personal_organization']}:initial();
 const client=organization.create({api,writer:tools.writes(api)});await client.refresh();instance='second';await assert.rejects(client.refresh(),/服务实例/);assert.equal(client.state.revision,0);
});
