// ---------------------------------------------------------------- "?" hot spots with tooltips
const TIPS = {
  'p-model': {t:'The loaded model that answers. "(auto / load on demand)" lets the server pick one or load the model named in the request.', r:'any loaded model', e:'Pick a different model to compare answers on the same prompt.'},
  'p-system': {t:'Standing instructions for the whole conversation: role, tone, rules, output format.', r:'free text', e:'"You are a concise assistant. Answer in at most three sentences."'},
  'p-temperature': {t:'How adventurous the next-word choice is. Low = focused and repeatable, high = varied and creative.', r:'0 – 2 (default 0.7)', e:'0.0–0.3 code / facts · 0.7 chat · 1.0+ stories and brainstorming'},
  'p-top_p': {t:'Only the most likely words whose probabilities add up to this value are considered. Change this or Temperature, not both.', r:'0 – 1 (default 0.95)', e:'0.95 normal · 0.5 very conservative · 1.0 off'},
  'p-top_k': {t:'Consider only the K most likely words at each step.', r:'0 – 200 (0 = off, default 40)', e:'40 normal · 1 = always the single best word (deterministic)'},
  'p-min_p': {t:'Drops words less likely than this fraction of the best word. A gentler alternative to Top K / Top P.', r:'0 – 1 (default 0.05)', e:'0.05 normal · 0.1 tighter output at high temperature'},
  'p-repeat_penalty': {t:'Discourages words that appeared recently. 1.0 means no penalty.', r:'1.0 – 2.0 (default 1.1)', e:'1.15 if a small model starts looping ("the the the …")'},
  'p-frequency_penalty': {t:'Penalty that grows with every repetition of a word. Negative values encourage repetition.', r:'−2 – 2 (default 0)', e:'0.3 to reduce repeated phrases'},
  'p-presence_penalty': {t:'Flat penalty for any word that has already been used, pushing the model towards new topics.', r:'−2 – 2 (default 0)', e:'0.5 for more varied vocabulary'},
  'p-max_tokens': {t:'Upper limit on the answer length. One token is about ¾ of an English word.', r:'−1 (no limit) or 1 – context length', e:'200 short answers · 2000 essays'},
  'p-seed': {t:'Fixes the random choices so the same prompt gives the same answer (same model and settings). Blank = random.', r:'blank or any whole number ≥ 0', e:'42 → reproducible results'},
  'p-stop': {t:'Generation ends as soon as one of these strings appears (it is not included). Comma separated; \\n is a line break.', r:'any text, comma separated', e:'###, \\n\\n  → stop at "###" or at the first blank line'},
  'p-reasoning': {t:'For thinking models: off skips thinking, low / medium cap it (≈500 / ≈2000 tokens), high / on let it think freely.', r:'model default · off · low · medium · high · on', e:'off for quick facts · high for maths and planning'},
  'p-mcp': {t:'Offers the tools of your MCP servers (Settings → MCP servers) to the model.', r:'on / off', e:'Ask "What files are in my project folder?" with a filesystem server connected.'},
  'p-mcp-confirm': {t:'Whether you approve every tool call before it runs.', r:'ask · run without asking', e:'Keep "ask" for tools that can change files or send data.'},
  'p-json': {t:'Forces the output to be valid JSON (enforced while generating).', r:'on / off', e:'Prompt: "List three colours as a JSON array."'},
  'p-cache': {t:'Keeps the already-processed start of the conversation in memory so follow-ups only process the new text.', r:'on / off (default on)', e:'Turn off only to measure raw prompt-processing speed.'},
  'p-schema': {t:'A JSON Schema the answer must match exactly (overrides JSON mode).', r:'JSON Schema text', e:'{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}'},
  // load dialog
  'l-device': {t:'Where the model runs: CPU only, GPU with every layer, or GPU with as many layers as fit.', r:'cpu · auto · gpu', e:'gpu for speed when the model fits in VRAM; auto for big models'},
  'l-gpuratio': {t:'Offload a share of the layers instead of counting them: 50% puts about half of the layers on the GPU. An exact layer count, when given, wins.', r:'0 – 100 %', e:'60% of a 33-layer model = 20 layers'},
  'l-gpulayers': {t:'Exact number of layers placed on the GPU. Blank = decided by Device.', r:'blank or 0 – layer count (999 = all)', e:'20 → 20 layers on the GPU, the rest on the CPU'},
  'l-ctx': {t:'How many tokens (prompt + conversation + answer) the model can keep in view. Bigger costs more memory (KV cache).', r:'512 – the model\'s maximum', e:'4096 chat · 16384+ long documents'},
  'l-bthreads': {t:'Threads for prompt processing. 0 = every logical processor (allowed by the affinity).', r:'0 or more', e:'0'},
  'l-affinity': {t:'Pin the compute threads to chosen processors: ranges such as 0-15,32, node:1 for one NUMA node, physical for one thread per core.', r:'blank = any', e:'node:0'},
  'l-prio': {t:'Windows priority of the compute threads.', r:'normal, high, low', e:'normal'},
  'l-threads': {t:'CPU threads used for generation. 0 = automatic (the physical cores; SMT siblings rarely help because generation is memory bound).', r:'0 or 1 – logical cores', e:'On a 192-thread CPU, 16–32 is often faster than all of them.'},
  'l-batch': {t:'Prompt tokens processed at once. Larger = faster prompt processing, slightly more memory.', r:'32 – 4096 (default 512)', e:'1024 for long prompts on a fast GPU'},
  'l-pooling': {t:'How the vectors of all tokens are combined into the one vector (or relevance score) of a text. Leave on auto unless a model card says otherwise.', r:'auto, mean, cls, last, rank, none', e:'rank for rerankers, cls for BGE, mean for most others'},
  'l-moecpu':{t:'Mixture-of-experts models are huge but only use a few experts per token. Keeping the expert weights in system memory while attention and the cache stay on the GPU lets a model far bigger than the VRAM run at a useful speed.', r:'-1 (all layers), 0 (off) or the number of layers', e:'-1 for a 30B MoE on a 24 GB card'},
  'l-moeexp': {t:'How many experts take part in each token. Fewer is faster but answers get worse.', r:'0 (model default) up to the number of experts', e:'4 instead of 8 for a quick draft'},
  'l-draft': {t:'A much smaller model of the same family guesses the next words and the main model checks them all at once. The answers stay exactly the same; they come faster (most for predictable text such as code).', r:'a downloaded model with the same vocabulary, under about 1/6 of the size', e:'Qwen3-0.6B for Qwen3-14B'},
  'l-ttl': {t:'Unload the model after this many idle minutes to free memory. It reloads on the next request.', r:'0 (never) – any number of minutes', e:'30 → unload after half an hour unused'},
  'l-kvk': {t:'Precision of the K half of the KV cache. q8_0 halves its memory, q4_0 quarters it, with a small quality cost.', r:'f16 · q8_0 · q4_0', e:'q8_0 for long contexts on small GPUs'},
  'l-kvv': {t:'Precision of the V half of the KV cache. A quantized V cache needs Flash attention (enabled automatically).', r:'f16 · q8_0 · q4_0', e:'q8_0 together with Flash attention'},
  'l-id': {t:'The name of this model in the API ("model": "…"). Default: the file name.', r:'free text', e:'my-qwen'},
  'l-main': {t:'GPU used for small tensors / the whole model in single-GPU mode.', r:'0 – number of GPUs − 1', e:'1 → use the second graphics card'},
  'l-conc': {t:'Requests generated at the same time (continuous batching). Further requests wait in a queue.', r:'1 – 16 (default 4)', e:'4 lets several API clients share one loaded model'},
  'l-flash': {t:'Memory-efficient, much faster attention for long prompts (a 24k-token prompt was 12x faster). Also required for a quantized V cache. Auto lets the engine switch it on when the device supports it.', r:'auto (default) · on · off', e:'Leave on auto. Turn off only to work around a driver problem.'},
  'l-mmap': {t:'Map the model file instead of copying it into memory: fast start, low load-time RAM.', r:'on / off (default on)', e:'Turn off if loading from a slow network drive.'},
  'l-mlock': {t:'Pin the model in RAM so it is never swapped out to disk.', r:'on / off (default off)', e:'On for servers with plenty of RAM.'},
  'l-emb': {t:'Load as an embedding model (vectors for search), not a chat model.', r:'on / off', e:'nomic-embed, bge … models'},
  'l-vision': {t:'Load the multimodal projector (mmproj) next to a vision model so it can read images.', r:'on / off (default on)', e:'Needs an mmproj file; costs a little extra memory.'},
  'l-splitmode': {t:'How a model is spread over several GPUs: by layers (default), by rows, or only on the first ticked GPU.', r:'layer · row · single GPU', e:'layer is the safest default'},
};
// the per-model defaults dialog edits the same sampling parameters
Object.assign(TIPS, {
  'g-temperature': TIPS['p-temperature'], 'g-top_p': TIPS['p-top_p'], 'g-top_k': TIPS['p-top_k'], 'g-min_p': TIPS['p-min_p'],
  'g-repeat': TIPS['p-repeat_penalty'], 'g-max': TIPS['p-max_tokens'], 'g-system': TIPS['p-system'],
});

(function initTips() {
  const tip = document.createElement('div');
  tip.id = 'tip'; tip.setAttribute('popover', 'manual'); document.body.appendChild(tip);   // a popover sits in the top layer, so it also shows above an open modal dialog
  const show = (qm, d) => {
    tip.innerHTML = `<div>${esc(d.t)}</div><div class="tr"><b>Range:</b> ${esc(d.r)}</div><div class="te"><b>Example:</b> ${esc(d.e)}</div>`;
    tip.style.display = 'block';
    try { tip.hidePopover(); tip.showPopover(); } catch (e) { /* no popover support: z-index only */ }
    const r = qm.getBoundingClientRect(), w = tip.offsetWidth, h = tip.offsetHeight;
    let x = r.left - w - 8; if (x < 8) x = Math.min(window.innerWidth - w - 8, r.right + 8);
    let y = Math.min(window.innerHeight - h - 8, Math.max(8, r.top - 6));
    tip.style.left = x + 'px'; tip.style.top = y + 'px';
  };
  const hide = () => { try { tip.hidePopover(); } catch (e) {} tip.style.display = 'none'; };
  for (const [id, d] of Object.entries(TIPS)) {
    const el = document.getElementById(id); if (!el) continue;
    let label = el.closest('label');
    if (!label) {
      let n = el.previousElementSibling;
      if (!n && el.parentElement) n = el.parentElement.previousElementSibling;
      if (n?.tagName === 'LABEL') label = n;
    }
    if (!label || label.querySelector('.qm')) continue;
    const qm = document.createElement('span');
    qm.className = 'qm'; qm.tabIndex = 0; qm.textContent = '?'; qm.setAttribute('role', 'note');
    qm.addEventListener('mouseenter', () => show(qm, d));
    qm.addEventListener('focus', () => show(qm, d));
    qm.addEventListener('mouseleave', hide);
    qm.addEventListener('blur', hide);
    qm.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); });
    label.appendChild(qm);
  }
})();

