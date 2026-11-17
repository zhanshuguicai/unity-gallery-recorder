//=============================================================================
// GalleryRecorder_Core.js
//=============================================================================
/*:
 * @plugindesc v0.1.0 回想自动录制 - 核心（RPG Maker MV / NW.js）。需要配合某个游戏的扩展包使用。
 * @author zhanshuguicai
 *
 * @help
 * 自动依次播放每段回想，每段单独录成 mp4，全部录完后统一压缩。
 * 本插件只是核心；具体游戏的“列出回想 / 启动 / 判断结束”由扩展包实现，
 * 扩展包放在本插件之后，调用 GalleryRecorder.register(adapter)。
 *
 * 热键：F8 开始 / 录完这段后停止，F7 放弃当前这段，F6 清空进度，
 *       F9 显示 / 隐藏状态，F10 立刻压缩已有录像。
 * 设置：<游戏目录>\GalleryRecorder\config.json（第一次运行自动生成）。
 * ffmpeg：<游戏目录>\GalleryRecorder\ffmpeg\ffmpeg.exe
 *
 * 说明见 https://github.com/zhanshuguicai/unity-gallery-recorder/tree/main/rpgmaker-mv
 */

var GalleryRecorder = (function () {
    'use strict';
    var VERSION = '0.1.3';
    var fs = require('fs');
    var path = require('path');
    var cp = require('child_process');

    var gameRoot = path.dirname(process.execPath);
    var baseDir = path.join(gameRoot, 'GalleryRecorder');

    // ---------------------------------------------------------------- 配置
    var DEFAULTS = {
        outputDir: '',                 // 空 = <游戏目录>\GalleryRecordings
        fps: 60,
        videoBitrate: 24000000,        // MediaRecorder 中间文件码率
        audioBitrate: 256000,
        muxArgs: '-c:v h264_mf -hw_encoding 1 -rate_control quality -quality 90 -c:a aac -b:a 256k',
        keepRawRecording: false,       // 保留浏览器录出的中间文件（.rec.webm）
        delayBetweenSeconds: 2,
        launchTimeoutSeconds: 60,
        stuckTimeoutSeconds: 180,
        maxMinutesPerEntry: 30,
        autoAdvance: true,
        autoAdvanceFallbackSeconds: 8, // 对话停在等待输入超过这么久，强制翻页
        blockSaves: true,              // 会话开始后禁止写存档（回放会弄乱存档状态），直到重启游戏
        compress: true,
        compressArgs: '-c:v hevc_mf -hw_encoding 1 -rate_control quality -quality 60 -c:a aac -b:a 160k',
        deleteOriginalAfterCompress: false,
        showOverlay: true,
        hotkeys: { start: 'F8', skip: 'F7', reset: 'F6', overlay: 'F9', compress: 'F10' }
    };

    function loadConfig() {
        var file = path.join(baseDir, 'config.json'), user = {};
        try { user = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { }
        var c = Object.assign({}, DEFAULTS, user);
        c.hotkeys = Object.assign({}, DEFAULTS.hotkeys, user.hotkeys || {});
        try { fs.mkdirSync(baseDir, { recursive: true }); rawWrite(file, JSON.stringify(c, null, 2)); } catch (e) { }
        return c;
    }

    // ---------------------------------------------------------------- 日志
    var logFile = path.join(baseDir, 'log.txt');
    function log(level, msg) {
        var line = '[' + new Date().toLocaleTimeString() + '] [' + level + '] ' + msg;
        (level === 'ERROR' ? console.error : console.log)('[GalleryRecorder] ' + msg);
        try { fs.appendFileSync(logFile, line + '\n'); } catch (e) { }
    }
    var Log = {
        info: function (m) { log('INFO', m); },
        warn: function (m) { log('WARN', m); },
        error: function (m) { log('ERROR', m); }
    };

    // ---------------------------------------------------------------- 存档保护
    // 回放剧情会改动开关 / 变量；会话开始后把所有写入 www\save 的操作变成空操作。
    var rawWrite = fs.writeFileSync.bind(fs);
    var savesBlocked = false, saveDir = null, blockedWrites = 0;
    function isSavePath(p) {
        if (!savesBlocked || typeof p !== 'string') return false;
        if (!saveDir) {
            try { saveDir = path.resolve(StorageManager.localFileDirectoryPath()).toLowerCase(); } catch (e) { return false; }
        }
        return path.resolve(p).toLowerCase().indexOf(saveDir) === 0;
    }
    ['writeFileSync', 'appendFileSync', 'unlinkSync', 'renameSync', 'copyFileSync'].forEach(function (name) {
        var orig = fs[name];
        if (typeof orig !== 'function') return;
        fs[name] = function (p) {
            if (isSavePath(p) || (name === 'renameSync' && isSavePath(arguments[1]))) { blockedWrites++; return undefined; }
            return orig.apply(fs, arguments);
        };
    });
    ['writeFile', 'appendFile', 'unlink', 'rename'].forEach(function (name) {
        var orig = fs[name];
        if (typeof orig !== 'function') return;
        fs[name] = function (p) {
            if (isSavePath(p)) {
                blockedWrites++;
                var cb = arguments[arguments.length - 1];
                if (typeof cb === 'function') setTimeout(function () { cb(null); }, 0);
                return undefined;
            }
            return orig.apply(fs, arguments);
        };
    });

    // ---------------------------------------------------------------- 声音截取
    // 所有连到扬声器（AudioDestinationNode）的节点，同时连一份到 MediaStreamDestination。
    // 必须在 WebAudio 初始化之前生效，所以放在插件加载时。
    var audioTaps = new Map();
    var nativeConnect = AudioNode.prototype.connect;
    function tapFor(ctx) {
        var t = audioTaps.get(ctx);
        if (!t) { t = ctx.createMediaStreamDestination(); audioTaps.set(ctx, t); }
        return t;
    }
    AudioNode.prototype.connect = function (dest) {
        var r = nativeConnect.apply(this, arguments);
        try {
            if (typeof AudioDestinationNode !== 'undefined' && dest instanceof AudioDestinationNode)
                nativeConnect.call(this, tapFor(dest.context));
        } catch (e) { }
        return r;
    };

    // ---------------------------------------------------------------- ffmpeg
    var ffmpegPath = path.join(baseDir, 'ffmpeg', 'ffmpeg.exe');
    function splitArgs(s) {
        var out = [], re = /"([^"]*)"|(\S+)/g, m;
        while ((m = re.exec(s || '')) !== null) out.push(m[1] !== undefined ? m[1] : m[2]);
        return out;
    }
    function runFfmpeg(args, errLog) {
        return new Promise(function (resolve) {
            var err = '';
            var p;
            try { p = cp.spawn(ffmpegPath, args, { windowsHide: true }); }
            catch (e) { Log.error('无法启动 ffmpeg：' + e.message); resolve(-1); return; }
            p.stderr.on('data', function (d) { if (err.length < 200000) err += d.toString(); });
            p.on('error', function (e) { err += e.message; });
            p.on('close', function (code) {
                if (code !== 0 && errLog) { try { rawWrite(errLog, err); } catch (e) { } }
                resolve(code === null ? -1 : code);
            });
        });
    }
    // MediaFoundation 编码器只接受 nv12。
    function fixMfArgs(a) {
        a = a || '';
        if (a.indexOf('_mf') >= 0 && a.indexOf('-pix_fmt') < 0) a = '-pix_fmt nv12 ' + a;
        if (a.indexOf('hevc') >= 0 && a.indexOf('-tag:v') < 0) a += ' -tag:v hvc1';
        return a;
    }

    // ---------------------------------------------------------------- 录制
    var MIME_CANDIDATES = [
        ['video/x-matroska;codecs=avc1,opus', 'mkv', true],
        ['video/webm;codecs=h264,opus', 'webm', true],
        ['video/webm;codecs=vp9,opus', 'webm', false],
        ['video/webm;codecs=vp8,opus', 'webm', false],
        ['video/webm', 'webm', false]
    ];
    var Recorder = {
        stream: null, mime: null, ext: 'webm', isH264: false,
        mr: null, raw: null, chain: null, startedAt: 0, bytes: 0,
        finalizeChain: Promise.resolve(), finalizePending: 0,

        ensureStream: function () {
            if (this.stream) return this.stream;
            var canvas = Graphics._canvas;
            if (!canvas || !canvas.captureStream) throw new Error('找不到游戏画布或不支持 captureStream');
            var v = canvas.captureStream(cfg.fps);
            var tracks = v.getVideoTracks().slice();
            var ctx = (typeof WebAudio !== 'undefined') ? WebAudio._context : null;
            if (ctx) tracks = tracks.concat(tapFor(ctx).stream.getAudioTracks());
            else Log.warn('没有 WebAudio，录像没有声音');
            if (audioTaps.size > 1) Log.warn('检测到 ' + audioTaps.size + ' 个 AudioContext，只录主音频');
            this.stream = new MediaStream(tracks);
            for (var i = 0; i < MIME_CANDIDATES.length; i++) {
                if (MediaRecorder.isTypeSupported(MIME_CANDIDATES[i][0])) {
                    this.mime = MIME_CANDIDATES[i][0]; this.ext = MIME_CANDIDATES[i][1]; this.isH264 = MIME_CANDIDATES[i][2];
                    break;
                }
            }
            Log.info('录制格式：' + this.mime + '，画面 ' + canvas.width + 'x' + canvas.height + '@' + cfg.fps);
            return this.stream;
        },

        start: function (mp4Path) {
            var self = this;
            var stream = this.ensureStream();
            this.raw = mp4Path.replace(/\.mp4$/i, '') + '.rec.' + this.ext;
            this.final = mp4Path;
            this.bytes = 0;
            rawWrite(this.raw, '');
            this.chain = Promise.resolve();
            var raw = this.raw;
            this.mr = new MediaRecorder(stream, { mimeType: this.mime, videoBitsPerSecond: cfg.videoBitrate, audioBitsPerSecond: cfg.audioBitrate });
            this.mr.ondataavailable = function (e) {
                if (!e.data || !e.data.size) return;
                var blob = e.data;
                self.bytes += blob.size;
                self.chain = self.chain.then(function () { return blob.arrayBuffer(); })
                    // 异步写，不阻塞游戏主线程（同步写在机械硬盘上会让游戏卡住几百毫秒到几秒）
                    .then(function (ab) {
                        return new Promise(function (res, rej) {
                            fs.appendFile(raw, Buffer.from(ab), function (err) { if (err) rej(err); else res(); });
                        });
                    })
                    .catch(function (err) { Log.error('写入录像失败：' + err.message); });
            };
            this.mr.onerror = function (e) { Log.error('MediaRecorder 出错：' + (e.error ? e.error.message : e)); };
            this.mr.start(1000);
            this.startedAt = performance.now();
            Log.info('开始录制 → ' + mp4Path);
        },

        get isRecording() { return !!this.mr; },
        get seconds() { return this.mr ? (performance.now() - this.startedAt) / 1000 : 0; },

        // keep=false 时丢弃这段。返回 Promise，在原始文件写完后 resolve。
        stop: function (keep) {
            var self = this, mr = this.mr, raw = this.raw, final = this.final, secs = this.seconds;
            this.mr = null;
            if (!mr) return Promise.resolve();
            return new Promise(function (resolve) {
                mr.onstop = function () {
                    self.chain.then(function () {
                        if (!keep) { tryDelete(raw); Log.info('已丢弃：' + path.basename(final)); resolve(); return; }
                        Log.info('停止录制，时长 ' + secs.toFixed(1) + 's，' + (self.bytes / 1048576).toFixed(1) + ' MB。后台转码中…');
                        self.enqueueFinalize(raw, final);
                        resolve();
                    });
                };
                try { mr.stop(); } catch (e) { mr.onstop(); }
            });
        },

        enqueueFinalize: function (raw, final) {
            var self = this;
            this.finalizePending++;
            var vArgs = this.isH264 ? ['-c:v', 'copy', '-c:a', 'aac', '-b:a', '256k']
                : splitArgs(fixMfArgs(cfg.muxArgs)).concat(['-r', String(cfg.fps)]);
            this.finalizeChain = this.finalizeChain.then(function () {
                var tmp = final + '.part.mp4', errLog = final.replace(/\.mp4$/i, '') + '.ffmpeg.log';
                var args = ['-hide_banner', '-loglevel', 'warning', '-y', '-i', raw].concat(vArgs, ['-movflags', '+faststart', tmp]);
                return runFfmpeg(args, errLog).then(function (code) {
                    if (code === 0 && fs.existsSync(tmp)) {
                        tryDelete(final);
                        fs.renameSync(tmp, final);
                        if (!cfg.keepRawRecording) tryDelete(raw);
                        Log.info('已保存：' + final);
                    } else {
                        tryDelete(tmp);
                        Log.error('转码失败 (exit ' + code + ')，保留原始文件 ' + raw + '，见 ' + errLog);
                    }
                    self.finalizePending--;
                });
            });
        }
    };

    function tryDelete(p) { try { if (fs.existsSync(p)) fs.unlinkSync(p); } catch (e) { } }

    // ---------------------------------------------------------------- 状态
    var cfg = null;
    var adapter = null;
    var outDir = null, progressPath = null;
    var done = {};               // key -> status
    var queue = [], index = -1, run = 1;
    var phase = 'Idle';          // Idle / Preparing / Playing / Waiting / Compressing
    var phaseSince = 0, entryStarted = 0, lastToken = null, lastTokenAt = 0;
    var stopAfterCurrent = false, status = '', compressStatus = '';
    var sessionEverStarted = false;
    var overlayVisible = true, overlayEl = null;

    function now() { return performance.now() / 1000; }
    function setPhase(p) { phase = p; phaseSince = now(); }

    var Context = {
        log: Log,
        get config() { return cfg; },
        get isRecording() { return Recorder.isRecording; },
        get phase() { return phase; },
        get gameRoot() { return gameRoot; },
        // 扩展包可以在播放中调用，表示“还在前进”（重置卡死计时）。
        markProgress: function () { lastTokenAt = now(); }
    };

    function register(a) {
        if (adapter) { Log.warn('已注册扩展包 ' + adapter.gameName + '，忽略 ' + a.gameName); return; }
        adapter = a;
        Log.info('扩展包：' + a.gameName);
        try { if (a.initialize) a.initialize(Context); } catch (e) { Log.error('扩展包初始化失败：' + e.stack); }
    }

    function loadProgress() {
        done = {};
        try {
            fs.readFileSync(progressPath, 'utf8').split(/\r?\n/).forEach(function (line) {
                var p = line.split('\t');
                if (p[0]) done[p[0]] = p[1] || 'ok';
            });
        } catch (e) { }
    }
    function markDone(key, st) {
        done[key] = st;
        try { fs.appendFileSync(progressPath, key + '\t' + st + '\t' + new Date().toISOString() + '\n'); } catch (e) { }
    }

    function sanitize(name) {
        return String(name).replace(/[\\/:*?"<>|\r\n\t]/g, '_').replace(/\s+/g, ' ').trim().slice(0, 120);
    }
    function fileFor(entry, r) {
        var base = sanitize(entry.fileName || entry.key);
        if (r > 1) base += (adapter.runSuffix ? adapter.runSuffix(r) : '_分支' + r);
        return path.join(outDir, base + '.mp4');
    }

    function beginSession() {
        if (!adapter) { status = '没有扩展包'; Log.warn(status); return; }
        if (!fs.existsSync(ffmpegPath)) { status = '找不到 ffmpeg：' + ffmpegPath; Log.error(status); return; }
        var reason = adapter.canBeginSession ? adapter.canBeginSession() : null;
        if (reason) { status = reason; Log.warn('现在不能开始：' + reason); return; }
        outDir = cfg.outputDir ? cfg.outputDir : path.join(gameRoot, 'GalleryRecordings');
        fs.mkdirSync(outDir, { recursive: true });
        progressPath = path.join(outDir, 'progress.txt');
        loadProgress();
        try { queue = adapter.buildQueue() || []; } catch (e) { Log.error('列出回想失败：' + e.stack); status = '列出回想失败'; return; }
        var todo = queue.filter(function (e) { return !done[e.key]; }).length;
        Log.info('队列 ' + queue.length + ' 段，其中待录 ' + todo + ' 段。输出：' + outDir);
        if (todo === 0) { status = '全部已录完（F6 清空进度可重录）'; return; }
        if (cfg.blockSaves && !savesBlocked) { savesBlocked = true; Log.info('已禁止写存档，直到重启游戏'); }
        sessionEverStarted = true;
        stopAfterCurrent = false;
        try { if (adapter.onSessionBegin) adapter.onSessionBegin(); } catch (e) { Log.error(e.stack); }
        index = -1;
        advance();
    }

    function advance() {
        run = 1;
        while (++index < queue.length && done[queue[index].key]) { }
        if (stopAfterCurrent) { endSession('已按要求停止'); return; }
        if (index >= queue.length) {
            Log.info('全部 ' + queue.length + ' 段已处理完。');
            endSession(null);
            return;
        }
        beginEntry();
    }

    function beginEntry() {
        var e = queue[index];
        status = '[' + (index + 1) + '/' + queue.length + '] 准备：' + e.title + (run > 1 ? '（第 ' + run + ' 遍）' : '');
        Log.info(status);
        setPhase('Preparing');
    }

    function startPlaying() {
        var e = queue[index];
        try { Recorder.start(fileFor(e, run)); }
        catch (err) { Log.error('无法开始录制：' + err.message); endSession('录制失败'); return; }
        try { adapter.launch(e, run); }
        catch (err) { Log.error('启动失败：' + err.stack); Recorder.stop(false); markDone(e.key, 'error'); waitThenAdvance(); return; }
        entryStarted = now(); lastToken = null; lastTokenAt = now();
        status = '[' + (index + 1) + '/' + queue.length + '] 播放：' + e.title;
        Log.info(status);
        setPhase('Playing');
    }

    function finishEntry(st, keep) {
        var e = queue[index];
        Recorder.stop(keep);
        if (st !== 'ok') { try { if (adapter.forceStop) adapter.forceStop(); } catch (err) { Log.error(err.stack); } }
        try { if (adapter.onEntryEnd) adapter.onEntryEnd(e, st); } catch (err) { Log.error(err.stack); }
        var replay = false;
        if (st === 'ok' && adapter.shouldReplay) { try { replay = adapter.shouldReplay(e, run) && run < 64; } catch (err) { Log.error(err.stack); } }
        if (replay) { run++; setPhase('Waiting'); pendingAction = beginEntry; return; }
        markDone(e.key, st);
        if (st !== 'ok') Log.warn('[' + (index + 1) + '] ' + e.title + '：' + st);
        waitThenAdvance();
    }

    var pendingAction = null;
    function waitThenAdvance() { pendingAction = advance; setPhase('Waiting'); }

    function endSession(msg) {
        if (Recorder.isRecording) Recorder.stop(false);
        try { if (adapter && adapter.onSessionEnd) adapter.onSessionEnd(); } catch (e) { Log.error(e.stack); }
        if (msg) { status = msg; Log.info(msg); setPhase('Idle'); return; }
        if (cfg.compress) startCompress();
        else { status = '全部录完：' + outDir; setPhase('Idle'); }
    }

    function listRecordings(dir) {
        if (!fs.existsSync(dir)) return [];
        return fs.readdirSync(dir).filter(function (f) { return /\.mp4$/i.test(f) && f.indexOf('.part.') < 0 && f.indexOf('.rec.') < 0; })
            .sort().map(function (f) { return path.join(dir, f); });
    }

    function startCompress() {
        if (!outDir) { outDir = cfg.outputDir ? cfg.outputDir : path.join(gameRoot, 'GalleryRecordings'); }
        setPhase('Compressing');
        compressStatus = '等待转码完成…';
        var dir = outDir, cdir = path.join(dir, 'Compressed');
        var args = splitArgs(fixMfArgs(cfg.compressArgs));
        // 先等所有转码结束再列文件，否则最后一段会漏掉。
        Recorder.finalizeChain.then(function () {
            var files = listRecordings(dir);
            Log.info('开始压缩 ' + files.length + ' 个文件…');
            fs.mkdirSync(cdir, { recursive: true });
            var ok = 0, before = 0, after = 0, i = 0;
            function next() {
                if (i >= files.length) {
                    compressStatus = '压缩完成 ' + ok + '/' + files.length + '，' + (before / 1048576).toFixed(0) + ' MB → ' + (after / 1048576).toFixed(0) + ' MB，目录：' + cdir;
                    Log.info(compressStatus);
                    status = compressStatus;
                    setPhase('Idle');
                    return;
                }
                var src = files[i++], dst = path.join(cdir, path.basename(src)), tmp = dst + '.part.mp4';
                var errLog = path.join(cdir, path.basename(src, '.mp4') + '.ffmpeg.log');
                compressStatus = '压缩中 ' + i + '/' + files.length + '：' + path.basename(src);
                Log.info(compressStatus);
                runFfmpeg(['-hide_banner', '-loglevel', 'warning', '-y', '-i', src, '-map', '0'].concat(args, ['-movflags', '+faststart', tmp]), errLog)
                    .then(function (code) {
                        if (code === 0 && fs.existsSync(tmp)) {
                            tryDelete(dst); fs.renameSync(tmp, dst); tryDelete(errLog);
                            before += fs.statSync(src).size; after += fs.statSync(dst).size; ok++;
                            if (cfg.deleteOriginalAfterCompress) tryDelete(src);
                        } else { tryDelete(tmp); Log.error('压缩失败 (exit ' + code + ')：' + src + '，见 ' + errLog); }
                        next();
                    });
            }
            next();
        });
    }

    // ---------------------------------------------------------------- 每帧
    function tick() {
        if (!cfg) return;
        var t = now();
        switch (phase) {
            case 'Preparing': {
                var ready = false;
                try { ready = adapter.prepare ? adapter.prepare(queue[index], run) : true; } catch (e) { Log.error(e.stack); }
                if (ready) { startPlaying(); break; }
                if (t - phaseSince > cfg.launchTimeoutSeconds) {
                    Log.warn('准备超时（' + cfg.launchTimeoutSeconds + 's），跳过');
                    try { if (adapter.forceStop) adapter.forceStop(); } catch (e) { Log.error(e.stack); }
                    markDone(queue[index].key, 'launch-timeout');
                    waitThenAdvance();
                }
                break;
            }
            case 'Playing': {
                try { if (adapter.whilePlaying) adapter.whilePlaying(); } catch (e) { Log.error(e.stack); }
                var finished = false;
                try { finished = adapter.isFinished(); } catch (e) { Log.error(e.stack); }
                if (finished) { finishEntry('ok', true); break; }
                if (adapter.progressToken) {
                    var tok = null;
                    try { tok = adapter.progressToken(); } catch (e) { }
                    if (tok !== lastToken) { lastToken = tok; lastTokenAt = t; }
                }
                if (t - lastTokenAt > cfg.stuckTimeoutSeconds) {
                    Log.warn('超过 ' + cfg.stuckTimeoutSeconds + 's 没有进展，视为卡住');
                    finishEntry('stuck', true);
                } else if (t - entryStarted > cfg.maxMinutesPerEntry * 60) {
                    Log.warn('超过 ' + cfg.maxMinutesPerEntry + ' 分钟，强制结束');
                    finishEntry('timeout', true);
                }
                break;
            }
            case 'Waiting':
                if (t - phaseSince >= cfg.delayBetweenSeconds && !Recorder.isRecording) {
                    var a = pendingAction; pendingAction = null;
                    if (a) a();
                }
                break;
        }
        updateOverlay();
    }

    // ---------------------------------------------------------------- 热键
    function onKey(e) {
        if (!cfg) return;
        var k = e.key, h = cfg.hotkeys;
        if (k === h.start) {
            if (phase === 'Idle') beginSession();
            else if (phase !== 'Compressing') { stopAfterCurrent = !stopAfterCurrent; status = stopAfterCurrent ? '录完这段后停止' : '继续'; Log.info(status); }
        } else if (k === h.skip) {
            if (phase === 'Playing') { Log.info('F7：放弃这段'); finishEntry('skipped', false); }
            else if (phase === 'Preparing') { markDone(queue[index].key, 'skipped'); try { adapter.forceStop && adapter.forceStop(); } catch (er) { } waitThenAdvance(); }
        } else if (k === h.reset) {
            if (phase === 'Idle') {
                var dir = cfg.outputDir ? cfg.outputDir : path.join(gameRoot, 'GalleryRecordings');
                tryDelete(path.join(dir, 'progress.txt')); done = {};
                status = '进度已清空'; Log.info(status);
            }
        } else if (k === h.overlay) {
            overlayVisible = !overlayVisible;
        } else if (k === h.compress) {
            if (phase === 'Idle') startCompress();
        } else return;
        e.preventDefault();
    }

    // ---------------------------------------------------------------- 浮层（DOM，不会录进画面）
    function updateOverlay() {
        if (!cfg.showOverlay) return;
        if (!overlayEl) {
            overlayEl = document.createElement('div');
            overlayEl.style.cssText = 'position:fixed;left:6px;top:6px;z-index:100000;pointer-events:none;' +
                'font:12px/1.5 "Microsoft YaHei",sans-serif;color:#fff;background:rgba(0,0,0,.6);padding:4px 8px;border-radius:4px;white-space:pre;max-width:90vw;overflow:hidden';
            document.body.appendChild(overlayEl);
        }
        var lines = ['GalleryRecorder ' + VERSION + (adapter ? ' · ' + adapter.gameName : ' · 没有扩展包')];
        if (phase === 'Idle') lines.push((status || '空闲') + '  [' + cfg.hotkeys.start + ' 开始]');
        else if (phase === 'Compressing') lines.push(compressStatus);
        else {
            lines.push(status);
            if (Recorder.isRecording) lines.push('● REC ' + Recorder.seconds.toFixed(0) + 's');
            if (Recorder.finalizePending > 0) lines.push('后台转码 ' + Recorder.finalizePending + ' 个');
            if (stopAfterCurrent) lines.push('（录完这段后停止）');
        }
        if (savesBlocked) lines.push('存档已禁用（录制会改动游戏状态，录完请重启游戏）' + (blockedWrites ? '，已拦截 ' + blockedWrites + ' 次' : ''));
        var text = lines.join('\n');
        if (overlayEl.textContent !== text) overlayEl.textContent = text;
        overlayEl.style.display = overlayVisible ? 'block' : 'none';
    }

    // ---------------------------------------------------------------- 挂载
    // 游戏报错只显示在画面上；记进日志，附带当前录到哪一段。
    function describeError(e) {
        if (!e) return String(e);
        return (e.stack || e.message || String(e)) + (e.filename ? ' @ ' + e.filename + ':' + e.lineno : '');
    }
    function logGameError(kind, e) {
        var where = (phase !== 'Idle' && queue && queue[index]) ? '（第 ' + (index + 1) + ' 段 ' + queue[index].title + '，' + phase + '）' : '';
        try { Log.error('游戏报错 ' + kind + where + '：' + describeError(e)); } catch (x) { }
    }
    if (SceneManager.catchException) {
        var _catchException = SceneManager.catchException;
        SceneManager.catchException = function (e) { logGameError('catchException', e); return _catchException.apply(this, arguments); };
    }
    if (SceneManager.onError) {
        var _onError = SceneManager.onError;
        SceneManager.onError = function (e) { logGameError('onError', e); return _onError.apply(this, arguments); };
    }
    // 缺素材时 MV 显示“Loading Error / Retry”并停住游戏，不走上面两个钩子
    if (Graphics.printLoadingError) {
        var _printLoadingError = Graphics.printLoadingError;
        Graphics.printLoadingError = function (url) {
            var u = url; try { u = decodeURIComponent(url); } catch (x) { }
            logGameError('缺少素材（游戏停在 Retry 画面）', { message: u });
            return _printLoadingError.apply(this, arguments);
        };
    }
    window.addEventListener('unhandledrejection',function (ev) { logGameError('unhandledrejection', ev.reason); });

    var _SceneManager_updateMain = SceneManager.updateMain;
    var lastMainAt = 0;
    SceneManager.updateMain = function () {
        // 主循环长时间没跑（窗口最小化、锁屏、睡眠、报错停住等）时记一笔，方便事后对照
        var pn = performance.now();
        if (lastMainAt && pn - lastMainAt > 10000) {
            var gap = (pn - lastMainAt) / 1000;
            Log.warn('游戏主循环停了 ' + Math.round(gap) + ' 秒（窗口最小化 / 锁屏 / 睡眠？），当前：' + phase);
            // 停住的时间不算进卡住 / 超时判断
            phaseSince += gap; entryStarted += gap; lastTokenAt += gap;
        }
        lastMainAt = pn;
        _SceneManager_updateMain.apply(this, arguments);
        try { tick(); } catch (e) { Log.error('tick: ' + e.stack); }
    };
    var _SceneManager_initialize = SceneManager.initialize;
    SceneManager.initialize = function () {
        _SceneManager_initialize.apply(this, arguments);
        try { fs.mkdirSync(baseDir, { recursive: true }); } catch (e) { }
        cfg = loadConfig();
        document.addEventListener('keydown', onKey);
        Log.info('核心 ' + VERSION + ' 已加载。ffmpeg：' + (fs.existsSync(ffmpegPath) ? '有' : '缺少 ' + ffmpegPath));
    };

    return {
        VERSION: VERSION,
        register: register,
        context: Context,
        log: Log
    };
})();
