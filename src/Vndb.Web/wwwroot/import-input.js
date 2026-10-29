'use strict';
// Shared presentation preflight; the existing server consumers remain authoritative.
globalThis.VnImport = Object.freeze({
  limits(info) {
    const limits = info.import_limits;
    if (!limits || !Number.isInteger(limits.maximum_bytes) || limits.maximum_bytes < 1024 || limits.maximum_bytes > 33554432 ||
        !Number.isInteger(limits.maximum_items) || limits.maximum_items < 1 || limits.maximum_items > 5000)
      throw Object.assign(new Error('无法发现有效导入限额，请刷新后重试。'), {code:'protocol_invalid'});
    return limits;
  },
  describe(limits) {
    return '每文件最多 ' + limits.maximum_bytes.toLocaleString() + ' 字节（' + (limits.maximum_bytes / 1048576).toFixed(2) + ' MiB）／' + limits.maximum_items +
      ' 项。新资料可拆分为较小文件；已发送或待确认批次保留原ID和回执，需恢复兼容限额后重试。metadata使用“JSON导入”，活动使用“会话”页。';
  },
  async read(file, kind, limits) {
    if (file.size > limits.maximum_bytes) throw Object.assign(new Error('文件超过服务端字节限制。' + this.describe(limits)), {code:'import_too_large'});
    let payload;
    try { payload = JSON.parse(await file.text()); }
    catch { throw Object.assign(new Error('JSON格式无效，请检查文件或重新导出；原件保留。'), {code:'invalid_json'}); }
    if (!payload || Array.isArray(payload) || typeof payload !== 'object') throw Object.assign(new Error('需要带明确种类的JSON对象。'), {code:'invalid_json'});
    if (kind === 'metadata' && payload.format === 'vn-library-activity') throw Object.assign(new Error('这是活动JSON，请到“会话”页导入。'), {code:'import_kind_mismatch'});
    if (kind === 'activity' && payload.format !== 'vn-library-activity') throw Object.assign(new Error('活动入口只接受活动JSON；metadata请到“JSON导入”页。'), {code:'import_kind_mismatch'});
    const count = kind === 'metadata' ? (Array.isArray(payload.items) ? payload.items.length : Array.isArray(payload.results) ? payload.results.length : 1) :
      ['metadata', 'items', 'installations', 'plays', 'play_sessions', 'play_edits'].reduce((sum, name) => sum + (Array.isArray(payload[name]) ? payload[name].length : 0), 0);
    if (count > limits.maximum_items) throw Object.assign(new Error('条目数超过服务端限制。' + this.describe(limits)), {code:'import_item_limit'});
    return payload;
  }
});
