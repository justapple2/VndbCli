'use strict';
(function(root, factory) {
  const tools = factory(); if (typeof module === 'object' && module.exports) module.exports = tools; else root.VnTools = tools;
})(globalThis, () => {
  const states = {preview:'待确认提交',queued:'等待处理',processing:'处理中',working:'正在补全',paused:'已暂停',complete:'批次处理结束',completed:'处理结束',
    imported:'已收录',existing:'复用已有资料',needs_review:'候选待核对',pending:'待核对',not_found:'暂未找到',error:'处理失败',invalid:'条目无效',
    active:'计时中',ended:'已结束',abandoned:'已中断',included:'已计入',excluded:'已排除',accepted:'已接收',applied:'已应用',conflict:'冲突待处理',not_started:'未游玩',playing:'游玩中',played:'已游玩',on_hold:'搁置',dropped:'放弃',
    deferred:'依赖待补全',dependency_pending:'依赖待补全',duplicate:'已处理，复用原结果',unconfirmed:'结果未确认',rejected:'明确拒绝',metadata:'公开资料',item:'书架条目',items:'书架条目',play:'游玩经历',plays:'游玩经历',session:'计时会话',play_edit:'个人编辑',play_edits:'个人编辑',play_session:'计时会话',play_sessions:'计时会话',installation:'存在标记'};
  const label = code => states[code] || '待核对（' + code + '）';
  function failure(error) {
    const code = error.code || '', status = error.status;
    const category = status === 401 || status === 403 ? '鉴权失败：请配置现有管理密钥。' : status === 409 ? '冲突：保留原稿，请明确重新读取后核对。' :
      status === 413 || /too_large|item_limit/.test(code) ? '超过服务限额：已发送批次保留原ID和原件，不要拆分重发。' :
      status === 429 ? '请求受限：请稍后按原操作重试。' : status === 400 || /invalid_json|kind_mismatch/.test(code) ? '格式或内容未通过校验：请检查原件与导入入口。' :
      /protocol|receipt/.test(code) ? '返回证明无效：结果尚未确认。' : !status || status >= 500 ? '服务暂时不可达或返回异常；已有显示与原稿保留。' : '请求未完成。';
    return category + (error.message ? ' ' + error.message : '');
  }
  function latest() { let version = 0; return {next() { const n = ++version; return () => n === version; }, cancel() { version++; }}; }
  async function request(path, {method = 'GET', body, operationId, signal} = {}, key = '') {
    const headers = {}; if (method !== 'GET') Object.assign(headers, {'X-Library-Request':'1','X-Library-Key':key});
    if(operationId)headers['Idempotency-Key']=operationId;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(path, {method, headers, body:body === undefined ? undefined : JSON.stringify(body),
      signal:signal ? AbortSignal.any([signal, AbortSignal.timeout(120000)]) : AbortSignal.timeout(120000)});
    const data = await response.json().catch(() => null);
    if (!response.ok) throw Object.assign(Error(data?.title || '请求失败 ' + response.status), {status:response.status, code:data?.code});
    if (data === null) throw Object.assign(Error('响应不是有效JSON。'), {code:'protocol_invalid'});
    return data;
  }
  function writes(send, uuid = () => crypto.randomUUID()) {
    const pending = new Map(), history = [];
    async function submit(entry) {
      if (entry.state === 'sending') throw Error('原操作正在提交，请等待。');
      if (entry.state === 'conflict' || entry.state === 'rejected') throw Object.assign(Error('原操作已明确拒绝；请重新读取核对后再操作。'), {status:entry.status});
      entry.state = 'sending';
      try {
        const result = await send(entry.path, {method:'POST', body:JSON.parse(entry.json), operationId:entry.id});
        if (!entry.validate(result)) throw Object.assign(Error('响应与原操作不符。'), {code:'protocol_invalid'});
        entry.state = 'applied'; pending.delete(entry.slot); return result;
      } catch (error) {
        entry.status = error.status;
        entry.state = error.status === 409 ? 'conflict' : error.status === 429 ? 'retryable' : error.status && error.status < 500 && error.status !== 408 ? 'rejected' : 'uncertain';
        throw error;
      }
    }
    return {
      async execute(slot, path, body, validate = value => value && typeof value === 'object') {
        const json = JSON.stringify(body ?? null);
        let entry = pending.get(slot);
        if (entry && (entry.path !== path || entry.json !== json)) throw Error('该目标的原操作仍待处理；请使用原操作重试或明确重新读取。');
        if (!entry) { entry = {slot,path,json:json ?? 'null',id:uuid(),state:'ready',validate}; pending.set(slot,entry); }
        return submit(entry);
      },
      retry(slot) { const entry = pending.get(slot); if (!entry) throw Error('没有待确认原操作。'); return submit(entry); },
      peek(slot) { const e = pending.get(slot); return e ? {state:e.state,id:e.id,path:e.path} : null; },
      proof(slot) { const e = pending.get(slot); return e ? {operation_id:e.id,path:e.path,body:JSON.parse(e.json),state:e.state} : null; },
      reload() { for (const [slot, entry] of pending) if (['conflict','rejected'].includes(entry.state)) {history.push(entry);pending.delete(slot);} },
      get unsettled() { return pending.size > 0; }, get archived() { return history.map(e => ({...e})); }
    };
  }
  function node(tag, text, cls) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; }
  function details(parent, title, value) { const d = node('details',undefined,'tool-evidence'); d.append(node('summary',title),node('pre',typeof value === 'string' ? value : JSON.stringify(value,null,2))); parent.append(d); }
  function action(parent, {label:caption, slot, path, body, validate, refresh}, writer) {
    let applied = false;
    const group = node('div',undefined,'tool-action'), button = node('button',caption,'secondary'), message = node('p',undefined,'notice');
    const retry = node('button','按原操作重试','secondary'); message.setAttribute('role','status'); message.hidden = true; retry.hidden = true;
    group.append(button,retry,message); parent.append(group);
    async function run(original) {
      button.disabled = retry.disabled = true; message.hidden = false; message.textContent = '正在提交；原操作与内容保持不变。';
      try {
        const result = await (original ? writer.retry(slot) : writer.execute(slot,path,body,validate));
        applied = true; retry.hidden = true; message.className = 'notice'; message.textContent = 'Web已确认此操作。';
        try { await refresh?.(result); } catch (error) { message.textContent = '操作已确认，刷新失败；请刷新列表，无需重发。' + failure(error); }
      } catch (error) {
        message.className = 'notice error'; const state = writer.peek(slot)?.state;
        retry.hidden = !['uncertain','retryable'].includes(state); message.textContent = failure(error) + (state === 'uncertain' ? ' 原操作结果不明，仅按原ID重试；离开页面前请保留源JSON。' : ' 当前内容与原操作保留。');
        if(writer.proof(slot))details(group,'本次原操作（保留供核对）',writer.proof(slot));
      } finally { button.disabled = applied || !!writer.peek(slot); retry.disabled = false; }
    }
    button.onclick = () => run(false); retry.onclick = () => run(true);
    if (writer.peek(slot)) { button.disabled = true; retry.hidden = !['uncertain','retryable'].includes(writer.peek(slot).state); message.hidden = false; message.textContent = '此目标有原操作待核对；明确拒绝可重新读取，限流或结果不明只重试原操作。'; details(group,'保留的原操作',writer.proof(slot)); }
    return button;
  }
  return Object.freeze({label,failure,latest,request,writes,node,details,action});
});
