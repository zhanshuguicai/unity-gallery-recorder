//=============================================================================
// GalleryRecorder_SAO2.js
//=============================================================================
/*:
 * @plugindesc v0.1.0 回想自动录制 - 扩展包：SAO~气息遮断魔法陷阱 Ⅱ。放在 GalleryRecorder_Core 之后。
 * @author zhanshuguicai
 *
 * @help
 * 回想在 4 个回想房间（Map081–084）里，每个 NPC 一段，选“完整剧情 / 仅限事件 / 放弃回顾”。
 * 本扩展包：
 * - 录每段的“仅限事件”（有（一）（二）（三）的每个都录）；
 *   没有“仅限事件”的录“包含录音 / 仅有录像”两个，或“完整剧情”。
 * - 跳过战斗回想（选项里有“进入战斗”的）。
 * - 直接执行回想 NPC 的事件页，不看解锁条件；并临时打开“第N篇回想解锁”开关（81–84）。
 * - 播放中打开游戏自带的 POR_AutoSkipMessage 自动翻页，剧情里的其他选项选第一项。
 * 在任意地图上（不在对话中）按 F8 开始。录完后不要存档，重启游戏。
 */

(function () {
    'use strict';
    if (typeof GalleryRecorder === 'undefined') { console.error('[GalleryRecorder_SAO2] 需要先加载 GalleryRecorder_Core'); return; }
    var fs = require('fs');
    var path = require('path');

    var ROOMS = [81, 82, 83, 84];
    var UNLOCK_SWITCHES = [81, 82, 83, 84];
    var STORY_VAR = 100;          // 剧情
    var ROOM_IDLE_FRAMES = 45;    // 回到回想房后连续空闲这么多帧算结束
    var OTHER_IDLE_SECONDS = 8;   // 停在其他地图、没有事件运行这么久也算结束

    var ctx = null, log = null;
    var current = null;           // 正在播放的条目
    var choiceFailed = false;
    var idleFrames = 0, idleOtherSince = 0;
    var prep = null;              // 准备阶段状态
    var snapshot = null;          // 会话开始时的位置 / 变量

    function clean(s) {
        return String(s || '')
            .replace(/\s*(if|en)\([^)]*\)/g, '')      // MPP_ChoiceEX 的条件语法
            .replace(/\\[A-Za-z]+\[[^\]]*\]/g, '')     // \c[n] 之类
            .replace(/\\[A-Za-z]+/g, '')                // \dac 之类
            .replace(/\\[{}|.!><^$]/g, '')
            .trim();
    }

    function wwwDir() { return path.resolve(StorageManager.localFileDirectoryPath(), '..'); }
    function readMap(id) {
        var f = path.join(wwwDir(), 'data', 'Map' + String(id).padStart(3, '0') + '.json');
        return JSON.parse(fs.readFileSync(f, 'utf8'));
    }

    function titleOf(list, choiceIndex) {
        var lines = [];
        for (var i = choiceIndex - 1; i >= 0; i--) {
            var c = list[i];
            if (c.code === 401) lines.unshift(c.parameters[0]);
            else if (c.code === 101) break;
        }
        var t = clean(lines.join(''));
        var m = /有关(.+?)的(回想|事件)/.exec(t) || /这是(.+?)的(监控录像|回想)/.exec(t) || /有关(.+?)[,，。]/.exec(t);
        t = m ? m[1] : t.slice(0, 24);
        return t.replace(/\//g, '-').replace(/\s+/g, '');
    }

    function buildQueue() {
        var q = [], seq = 0, skippedBattles = 0;
        ROOMS.forEach(function (roomId) {
            var map = readMap(roomId);
            var events = map.events.filter(Boolean).sort(function (a, b) { return a.y - b.y || a.x - b.x; });
            events.forEach(function (ev) {
                var found = null;
                ev.pages.forEach(function (page, pi) {
                    page.list.forEach(function (cmd, ci) {
                        if (cmd.code === 102 && cmd.parameters[0].some(function (s) { return /放弃回顾/.test(s); }))
                            found = { pageIndex: pi, choiceIndex: ci, options: cmd.parameters[0] };
                    });
                });
                if (!found) return;
                var page = ev.pages[found.pageIndex];
                var battle = page.list.some(function (c) {
                    return c.code === 102 && c.parameters[0].some(function (s) { return /进入战斗/.test(s); });
                });
                if (battle) { skippedBattles++; return; }
                var opts = found.options.map(clean);
                var pick = opts.filter(function (o) { return /仅限事件/.test(o); });
                if (!pick.length) pick = opts.filter(function (o) { return /包含录音|仅有录像/.test(o); });
                if (!pick.length) pick = opts.filter(function (o) { return /完整剧情/.test(o); });
                if (!pick.length) { log.warn('Map' + roomId + ' EV' + ev.id + ' 没有可录的选项：' + opts.join('/')); return; }
                var title = titleOf(page.list, found.choiceIndex);
                seq++;
                pick.forEach(function (opt) {
                    var label = pick.length > 1 || !/仅限事件/.test(opt) ? opt : '';
                    q.push({
                        key: roomId + '-' + ev.id + '-' + opt,
                        title: title + (label ? ' ' + label : ''),
                        fileName: String(seq).padStart(3, '0') + '_' + title + (label ? '_' + label : ''),
                        data: { mapId: roomId, eventId: ev.id, pageIndex: found.pageIndex, option: opt, x: ev.x, y: ev.y }
                    });
                });
            });
        });
        log.info('回想 ' + seq + ' 段（跳过战斗 ' + skippedBattles + ' 段），共 ' + q.length + ' 个录像');
        return q;
    }

    // ------------------------------------------------------------ 自动选择
    var _setupChoices = Game_Interpreter.prototype.setupChoices;
    Game_Interpreter.prototype.setupChoices = function (params) {
        _setupChoices.apply(this, arguments);
        if (!current || !$gameMessage.isChoice()) return;
        var texts = $gameMessage._choices.map(clean), idx = 0;
        if (texts.some(function (t) { return /放弃回顾/.test(t); })) {
            idx = texts.indexOf(current.data.option);
            if (idx < 0) {
                log.error('选项里没有“' + current.data.option + '”：' + texts.join('/'));
                choiceFailed = true;
                idx = texts.findIndex(function (t) { return /放弃/.test(t); });
                if (idx < 0) idx = texts.length - 1;
            }
        } else {
            log.info('剧情选项 [' + texts.join(' / ') + '] → ' + texts[0]);
        }
        $gameMessage.onChoice(idx);
        $gameMessage._choices = [];
    };

    // 对话停在等待输入太久时强制翻页（POR_AutoSkipMessage 没覆盖到的情况）。
    var _isTriggered = Window_Message.prototype.isTriggered;
    Window_Message.prototype.isTriggered = function () {
        if (_isTriggered.apply(this, arguments)) return true;
        if (!current || !ctx.config.autoAdvance) return false;
        if (this.pause) {
            this._grPause = (this._grPause || 0) + 1;
            if (this._grPause > ctx.config.autoAdvanceFallbackSeconds * 60) { this._grPause = 0; return true; }
        } else this._grPause = 0;
        return false;
    };

    function setAuto(on) {
        if (typeof PORParameters !== 'undefined' && PORParameters.autoSkipMessage)
            PORParameters.autoSkipMessage.active = on;
    }

    // ------------------------------------------------------------ 状态判断
    function mapSceneIdle() {
        var s = SceneManager._scene;
        return s instanceof Scene_Map && s.isActive() && !SceneManager.isSceneChanging() &&
            !$gamePlayer.isTransferring() && !$gameMap.isEventRunning() && !$gameMessage.isBusy();
    }
    function inRoom() { return ROOMS.indexOf($gameMap.mapId()) >= 0; }

    function transferTo(mapId, x, y) {
        $gamePlayer.reserveTransfer(mapId, x, y, 8, 0);
    }

    // ------------------------------------------------------------ 扩展包
    var adapter = {
        gameName: 'SAO~气息遮断魔法陷阱 Ⅱ',

        initialize: function (c) { ctx = c; log = c.log; },

        canBeginSession: function () {
            if (!(SceneManager._scene instanceof Scene_Map)) return '请先读档进入地图';
            if (!mapSceneIdle()) return '请在没有事件、没有对话时按 F8';
            return null;
        },

        buildQueue: buildQueue,

        onSessionBegin: function () {
            snapshot = {
                mapId: $gameMap.mapId(), x: $gamePlayer.x, y: $gamePlayer.y,
                story: $gameVariables.value(STORY_VAR),
                switches: UNLOCK_SWITCHES.map(function (id) { return $gameSwitches.value(id); })
            };
            UNLOCK_SWITCHES.forEach(function (id) { $gameSwitches.setValue(id, true); });
        },

        prepare: function (entry) {
            if (!prep || prep.entry !== entry) prep = { entry: entry, requested: false, idle: 0 };
            var d = entry.data;
            if (!mapSceneIdle()) { prep.idle = 0; return false; }
            if ($gameMap.mapId() !== d.mapId) {
                if (!prep.requested) { transferTo(d.mapId, d.x, d.y + 1); prep.requested = true; }
                return false;
            }
            if (++prep.idle < 30) return false;
            return true;
        },

        launch: function (entry) {
            var d = entry.data;
            var page = $dataMap.events[d.eventId].pages[d.pageIndex];
            $gameVariables.setValue(STORY_VAR, snapshot.story);
            UNLOCK_SWITCHES.forEach(function (id) { $gameSwitches.setValue(id, true); });
            current = entry; choiceFailed = false; idleFrames = 0; idleOtherSince = 0;
            setAuto(ctx.config.autoAdvance);
            $gameMap._interpreter.setup(page.list, d.eventId);
        },

        whilePlaying: function () {
            if (ctx.config.autoAdvance) setAuto(true);
            if (mapSceneIdle()) {
                idleFrames++;
                if (!inRoom() && !idleOtherSince) idleOtherSince = performance.now();
            } else { idleFrames = 0; idleOtherSince = 0; }
        },

        isFinished: function () {
            if (choiceFailed) return true;
            if (inRoom()) return idleFrames >= ROOM_IDLE_FRAMES;
            if (idleOtherSince && performance.now() - idleOtherSince > OTHER_IDLE_SECONDS * 1000) {
                log.warn('停在 Map' + $gameMap.mapId() + ' 且没有事件在运行，视为结束');
                return true;
            }
            return false;
        },

        progressToken: function () {
            var it = $gameMap._interpreter, s = String($gameMap.mapId());
            while (it) { s += ':' + it._index; it = it._childInterpreter; }
            return s + ':' + $gameMessage.allText().length + ':' + (SceneManager._scene && SceneManager._scene.constructor.name);
        },

        forceStop: function () {
            if (SceneManager._scene instanceof Scene_Battle) BattleManager.abort();
            $gameMap._interpreter.clear();
            $gameMessage.clear();
            $gameScreen.clearPictures();
            $gameScreen.startTint([0, 0, 0, 0], 1);
            $gameScreen.startFadeIn(1);
            AudioManager.stopBgs();
            AudioManager.stopSe();
            if (snapshot) $gameVariables.setValue(STORY_VAR, snapshot.story);
            var d = current ? current.data : null;
            if (d && !($gameMap.mapId() === d.mapId)) transferTo(d.mapId, d.x, d.y + 1);
            current = null;
            return true;
        },

        onEntryEnd: function () { current = null; },

        onSessionEnd: function () {
            current = null;
            setAuto(false);
            if (!snapshot) return;
            $gameVariables.setValue(STORY_VAR, snapshot.story);
            UNLOCK_SWITCHES.forEach(function (id, i) { $gameSwitches.setValue(id, snapshot.switches[i]); });
            if (SceneManager._scene instanceof Scene_Map) transferTo(snapshot.mapId, snapshot.x, snapshot.y);
            log.info('已回到开始的位置。录制过程改动了游戏状态，请不要存档，重启游戏。');
        }
    };

    GalleryRecorder.register(adapter);
})();
