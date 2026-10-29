const { test } = require('node:test');
const assert = require('node:assert/strict');
require('../../src/Vndb.Web/wwwroot/import-input.js');
const rules = globalThis.VnImport;
const limits = { maximum_bytes: 1024, maximum_items: 2 };
const metadata = { format: 'vn-library-batch', batch_id: 'original-test-id', items: [{ id: 'v17' }] };
function file(payload, size) { const text = typeof payload === 'string' ? payload : JSON.stringify(payload); return { size: size ?? Buffer.byteLength(text), text: async () => text }; }

test('discovery is required and does not silently substitute old limits', () => {
  assert.throws(() => rules.limits({}));
  assert.throws(() => rules.limits({ import_limits: { ...limits, maximum_items: 0 } }));
  assert.equal(rules.limits({ import_limits: { ...limits, maximum_bytes: 10485760 } }).maximum_bytes, 10485760);
  assert.match(rules.describe(limits), /原ID/);
});
test('file byte limit is inclusive and excess is rejected before reading', async () => {
  assert.equal((await rules.read(file(metadata, 1024), 'metadata', limits)).batch_id, metadata.batch_id);
  let reads = 0;
  await assert.rejects(rules.read({ size: 1025, text: async () => { reads++; return '{}'; } }, 'metadata', limits));
  assert.equal(reads, 0);
});
test('counts metadata and aggregate activity entries without changing IDs', async () => {
  const batch = { ...metadata, items: [{}, {}, {}] };
  await assert.rejects(rules.read(file(batch), 'metadata', limits), /条目数/);
  const activity = { format: 'vn-library-activity', batch_id: 'original-activity-test', items: [{}], plays: [{}], play_sessions: [{}] };
  await assert.rejects(rules.read(file(activity), 'activity', limits), /条目数/);
  assert.equal((await rules.read(file(activity), 'activity', { ...limits, maximum_items: 3 })).batch_id, activity.batch_id);
  assert.equal(activity.play_sessions.length, 1);
});
test('kind and malformed JSON lead to actionable correct-entry messages', async () => {
  await assert.rejects(rules.read(file('{'), 'metadata', limits), /JSON格式无效/);
  await assert.rejects(rules.read(file(metadata), 'activity', limits), /JSON导入/);
  await assert.rejects(rules.read(file({ format: 'vn-library-activity' }), 'metadata', limits), /会话/);
});
