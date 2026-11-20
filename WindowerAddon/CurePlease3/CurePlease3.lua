_addon.name = 'CurePlease3'
_addon.author = 'Daniel_H, CurePlease v3 team'
_addon.version = '2.0 Windower'
_addon_description = 'Party buff reports, cast outcome events, and landing reports for CurePlease v3'
_addon.commands = {'cp3'}

-- Protocol v2/v3 (UDP, default 127.0.0.1:19771):
--   CUREPLEASE_buffs_<Name>_<id1,id2,...>            own-party buff dump (from 0x076)
--   CUREPLEASE_bufftimes_<Name>_<id:seconds,...>     local buff remaining times (from 0x63)
--   CUREPLEASE_action2_<CharName>_<spellId>_<names>  caster-keyed landing report (who a cast hit)
--   CUREPLEASE_casting3_<CharName>_<spellId>_started|finished|interrupted|rejected
--   CUREPLEASE_casting2_<CharName>_finished|interrupted (legacy consumers)
--   CUREPLEASE_confirmed_<CharName>                  handshake reply to //cp3 verify (name-keyed)
--   CUREPLEASE_confirmed                             legacy handshake (kept for older apps)
--   CUREPLEASE_chat_<Name>_<mode>_<color>_<text>     chat feed (port 19770, fixed)
-- Inbound (app -> game): //cp3 log <text> prints an app alert to the LOCAL chat
-- log only (add_to_chat never reaches the server).

local port = 19771
local ip = "127.0.0.1"

local socket = require("socket")
require('packets')

-- One persistent socket for the session (the 1.x addon opened one per message).
local udp = assert(socket.udp())
udp:settimeout(1)

local function send(msg, target_port)
    udp:sendto(msg, ip, target_port or port)
end

local function player_name()
    local player = windower.ffxi.get_player()
    return (player and player.name) or '?'
end

-- 0x63 timestamps use the same epoch conversion as Singer: buff_ts / 60 is
-- seconds within the Vana'diel clock; this offset converts them to Unix time.
local time = os.time()
local vana_time = time - 1009810800
local bufftime_offset = math.floor(time - (vana_time * 60 % 0x100000000) / 60)

-- 0x076: party buff update — 5 member blocks, 48 bytes each.
local function send_party_buffs(data)
    for k = 0, 4 do
        local uid = data:unpack('I', k * 48 + 5)
        if uid and uid ~= 0 then
            local mob = windower.ffxi.get_mob_by_id(uid)
            local name = mob and mob.name
            if name then
                local buffs = {}
                for i = 1, 32 do
                    local lo = data:byte(k * 48 + 5 + 16 + i - 1)
                    local hi = math.floor(data:byte(k * 48 + 5 + 8 + math.floor((i - 1) / 4)) / 4 ^ ((i - 1) % 4)) % 4
                    local buff = lo + 256 * hi
                    if buff ~= 255 and buff ~= 0 then
                        table.insert(buffs, buff)
                    end
                end
                send("CUREPLEASE_buffs_" .. name .. "_" .. table.concat(buffs, ","))
            end
        end
    end
end

-- 0x63 type 9: local buff ids and expiry timestamps. Unlike party packet
-- 0x076, this preserves each slot's remaining time, including duplicate songs.
local function send_local_buff_times(data)
    local entries = {}
    local now = os.time()
    for n = 1, 32 do
        local buff_id = data:unpack('H', n * 2 + 7)
        local buff_ts = data:unpack('I', n * 4 + 69)
        if buff_ts == 0 then
            break
        elseif buff_id ~= 255 then
            local remaining = math.max(0, math.floor(buff_ts / 60 + bufftime_offset - now))
            table.insert(entries, tostring(buff_id) .. ':' .. tostring(remaining))
        end
    end
    send('CUREPLEASE_bufftimes_' .. player_name() .. '_' .. table.concat(entries, ','))
end

windower.register_event('incoming chunk', function(id, data)
    if id == 0x076 then
        send_party_buffs(data)
    elseif id == 0x63 and data:byte(5) == 9 then
        send_local_buff_times(data)
    end
end)

windower.register_event('addon command', function(input, ...)
    local args = {...}
    local cmd = string.lower(input)
    if cmd == "settings" then
        if args[1] and args[2] then
            ip = args[1]
            port = tonumber(args[2])
            windower.add_to_chat(1, '\31\200[\31\05CurePlease3\31\200]\31\207 NETWORK UPDATE: IP ' .. ip .. " / Port " .. port)
        end
    elseif cmd == "check" then
        windower.add_to_chat(1, '\31\200[\31\05CurePlease3\31\200]\31\207 IP ' .. ip .. " / Port " .. port)
    elseif cmd == "verify" then
        send("CUREPLEASE_confirmed_" .. player_name())
        send("CUREPLEASE_confirmed") -- legacy format for older apps
    elseif cmd == "probe" then
        -- Ground-truth dump for memory RE: write the Windower player table
        -- (plus JP record for the main job) to a file the app side can read.
        local p = windower.ffxi.get_player()
        local out = {}
        table.insert(out, "player table keys/values:")
        if p then
            local keys = {}
            for k in pairs(p) do table.insert(keys, k) end
            table.sort(keys)
            for _, k in ipairs(keys) do
                local v = p[k]
                if type(v) ~= 'table' then
                    table.insert(out, string.format("  %s = %s", tostring(k), tostring(v)))
                end
            end
            if p.job_points and p.main_job then
                local jp = p.job_points[string.lower(p.main_job)]
                if jp then
                    table.insert(out, string.format("job_points[%s]: cp=%s jp=%s spent=%s",
                        string.lower(p.main_job), tostring(jp.capacity_points), tostring(jp.job_points), tostring(jp.spent_job_points)))
                end
            end
        else
            table.insert(out, "get_player() = nil")
        end
        local f = io.open(windower.addon_path .. 'probe_' .. player_name() .. '.txt', 'w')
        if f then f:write(table.concat(out, '\n')) f:close() end
        windower.add_to_chat(207, '\31\200[\31\05CurePlease\31\200]\31\207 probe written')
    elseif cmd == "log" then
        -- App-driven in-game alert: prints to the LOCAL chat log only
        -- (add_to_chat never reaches the FFXI server). Mode 207 + colored tag.
        local text = table.concat(args, " ")
        if #text > 0 then
            windower.add_to_chat(207, '\31\200[\31\05CurePlease\31\200]\31\207 ' .. text)
        end
    end
end)

-- Chat feed for the app's chat log window (fixed port 19770).
windower.register_event('incoming text', function(a, b, c, d, e)
    local text = type(a) == 'string' and a or (type(b) == 'string' and b or nil)
    if not text then return end
    local nums = {}
    for _, v in ipairs({a, b, c, d, e}) do
        if type(v) == 'number' then table.insert(nums, v) end
    end
    local mode = nums[#nums] or 0
    local color = nums[1] or 0
    text = text:gsub('[\r\n]', ' ')
    if #text > 480 then text = text:sub(1, 480) end
    send('CUREPLEASE_chat_' .. player_name() .. '_' .. tostring(mode) .. '_' .. tostring(color) .. '_' .. text, 19770)
end)

local function action_spell_id(data)
    if data.category == 4 then
        return data.param or 0
    end

    -- Begin/reject packets carry the spell in the target action, not in data.param.
    if data.targets ~= nil then
        for _, target in pairs(data.targets) do
            if target.actions ~= nil then
                for _, action in pairs(target.actions) do
                    if action.param and action.param ~= 0 then
                        return action.param
                    end
                end
            end
        end
    end
    return 0
end

local function send_cast_event(outcome, spell_id)
    send('CUREPLEASE_casting3_' .. player_name() .. '_' .. tostring(spell_id or 0) .. '_' .. outcome)
end

-- Cast outcomes + per-target landing reports for the local player's actions.
windower.register_event('action', function(data)
    if data.actor_id ~= windower.ffxi.get_player().id then return end

    if data.category == 4 then
        -- Spell cast completed.
        local spell_id = action_spell_id(data)
        send_cast_event('finished', spell_id)
        send('CUREPLEASE_casting2_' .. player_name() .. '_finished') -- legacy consumers

        -- Per-target landing report: exactly who the cast hit (AoE coverage data).
        if data.targets ~= nil then
            local names = {}
            for _, target in pairs(data.targets) do
                local mob = windower.ffxi.get_mob_by_id(target.id)
                if mob and mob.name then
                    table.insert(names, mob.name)
                end
            end
            if #names > 0 then
                send('CUREPLEASE_action2_' .. player_name() .. '_' .. tostring(spell_id) .. '_' .. table.concat(names, ','))
            end
        end
    elseif data.category == 8 or data.category == 9 then
        local spell_id = action_spell_id(data)
        if data.param == 24931 then
            send_cast_event('started', spell_id)
        elseif data.param == 28787 then
            send_cast_event('interrupted', spell_id)
            send('CUREPLEASE_casting2_' .. player_name() .. '_interrupted') -- legacy consumers
        else
            send_cast_event('rejected', spell_id)
        end
    end
end)
