--[[
   Copyright (c) The AutoC&C Developers and Contributors
   This file is part of AutoC&C, which is free software. It is made
   available to you under the terms of the GNU General Public License
   as published by the Free Software Foundation, either version 3 of
   the License, or (at your option) any later version. For more
   information, see LICENSE.

   The duel lab: every combat unit against every other one, played out by the engine on open
   ground, so a matchup is a measured fight rather than arithmetic on the rules. One DUEL| line
   per fight goes to lua.log; scripts/duel-lab.ps1 installs this map, runs it headless and turns
   the log into a table. Blue always starts on the left and Red on the right.
]]

-- duel-lab-config.lua, written by the runner, may narrow any of these for a quick check.
Mobile = LabMobile or { "e1", "e2", "e3", "e4", "e5", "rmbo", "jeep", "apc", "mtnk", "htnk", "msam",
	"bggy", "bike", "ltnk", "ftnk", "stnk", "arty", "mlrs", "orca", "heli" }
Towers = LabTowers or { "gtwr", "atwr", "gun", "obli", "sam" }
Targets = LabTargets or { "harv", "proc" }
Scenarios = LabScenarios or { ["1v1"] = true, cost = true, ["cost-spotted"] = true, tower = true, raid = true }

LaneCount = 6
LanePitch = 26
FirstLaneRow = 14
BlueEdge = 6
Columns = 6
Gap = 18
RedEdge = BlueEdge + Columns + Gap
Budget = 3600
TowerBudget = 2400
RaidBudget = 1200
TimeLimit = DateTime.Seconds(180)
QuietLimit = DateTime.Seconds(45)
Interval = 5

Queue = { }
Lanes = { }
Next = 1
Done = 0

LaneRow = function(lane)
	return FirstLaneRow + (lane - 1) * LanePitch
end

CountFor = function(actorType, budget)
	return math.max(1, math.floor(budget / Actor.Cost(actorType) + 0.5))
end

Add = function(scenario, a, na, b, nb, static, spotted)
	if Scenarios[scenario] then
		Queue[#Queue + 1] = { id = #Queue + 1, scenario = scenario, a = a, na = na, b = b, nb = nb, static = static, spotted = spotted }
	end
end

BuildQueue = function()
	for i = 1, #Mobile do
		for j = i + 1, #Mobile do
			local a, b = Mobile[i], Mobile[j]
			Add("1v1", a, 1, b, 1, false, false)
			Add("cost", a, CountFor(a, Budget), b, CountFor(b, Budget), false, false)
			Add("cost-spotted", a, CountFor(a, Budget), b, CountFor(b, Budget), false, true)
		end
	end

	for _, a in ipairs(Mobile) do
		for _, t in ipairs(Towers) do
			Add("tower", a, CountFor(a, TowerBudget), t, 1, true, false)
		end
		for _, t in ipairs(Targets) do
			Add("raid", a, CountFor(a, RaidBudget), t, 1, true, false)
		end
	end
end

Spawn = function(owner, tag, actorType, count, leftEdge, row, facing)
	local actors = { }
	local rows = math.ceil(count / Columns)
	local top = row - math.floor((rows - 1) / 2)
	for i = 0, count - 1 do
		local cell = CPos.New(leftEdge + (i % Columns), top + math.floor(i / Columns))
		local a = Actor.Create(actorType, true, { Owner = owner, Location = cell, Facing = facing })
		a.AddTag(tag)
		actors[#actors + 1] = a
	end
	return actors
end

Alive = function(list)
	local n = 0
	for _, a in ipairs(list) do
		if not a.IsDead then
			n = n + 1
		end
	end
	return n
end

-- Credits still standing, counting a damaged unit at its remaining share of its price.
ValueLeft = function(list)
	local v = 0
	for _, a in ipairs(list) do
		if not a.IsDead and a.MaxHealth > 0 then
			v = v + Actor.Cost(a.Type) * a.Health / a.MaxHealth
		end
	end
	return math.floor(v + 0.5)
end

Distance = function(a, b)
	if a == nil or b == nil or a.IsDead or b.IsDead then
		return -1
	end
	local dx = a.Location.X - b.Location.X
	local dy = a.Location.Y - b.Location.Y
	return math.floor(math.sqrt(dx * dx + dy * dy) * 10 + 0.5) / 10
end

-- side is the side that should have done the damage; victimTag names the victim's side.
OnHit = function(d, side, victimTag, victim, attacker, damage)
	if d.done then
		return
	end

	local live = attacker ~= nil and not attacker.IsDead
	if live and attacker.HasTag(victimTag) then
		d.friendly = d.friendly + damage
		return
	end

	local now = DateTime.GameTime
	d.lastDamage = now
	local s = d[side]
	s.dealt = s.dealt + damage
	if s.firstTick == nil then
		s.firstTick = now - d.start
		s.firstDistance = Distance(attacker, victim)
	end
	if live then
		local range = Distance(attacker, victim)
		if range > s.maxDistance then
			s.maxDistance = range
		end
	end
end

Track = function(d, list, hitBy, victimTag)
	for _, a in ipairs(list) do
		Trigger.OnDamaged(a, function(self, attacker, damage)
			OnHit(d, hitBy, victimTag, self, attacker, damage)
		end)
		Trigger.OnKilled(a, function()
			if not d.done then
				d[hitBy].kills = d[hitBy].kills + 1
			end
		end)
	end
end

Side = function()
	return { dealt = 0, kills = 0, firstTick = nil, firstDistance = -1, maxDistance = -1 }
end

Start = function(lane, spec)
	local row = LaneRow(lane)
	local d = { spec = spec, lane = lane, start = DateTime.GameTime, lastDamage = DateTime.GameTime,
		blue = Side(), red = Side(), friendly = 0, extras = { }, done = false }
	Lanes[lane] = d

	d.blueActors = Spawn(Blue, "blue", spec.a, spec.na, BlueEdge, row, Angle.East)
	if spec.static then
		-- Placed complete: a building still playing its make animation holds build-incomplete,
		-- which pauses every defence's weapon.
		local target = Actor.Create(spec.b, true, { Owner = Red, Location = CPos.New(RedEdge + 1, row - 1) })
		target.AddTag("red")
		d.redActors = { target }
	else
		d.redActors = Spawn(Red, "red", spec.b, spec.nb, RedEdge, row, Angle.West)
	end

	-- Damage to Red's actors is Blue's, and the reverse.
	Track(d, d.redActors, "blue", "red")
	Track(d, d.blueActors, "red", "blue")

	if spec.spotted then
		for _, x in ipairs({ BlueEdge + 3, BlueEdge + Columns + math.floor(Gap / 2), RedEdge + 3 }) do
			d.extras[#d.extras + 1] = Actor.Create("camera", true, { Owner = Blue, Location = CPos.New(x, row) })
			d.extras[#d.extras + 1] = Actor.Create("camera", true, { Owner = Red, Location = CPos.New(x, row) })
		end
	end

	d.valueBlue = ValueLeft(d.blueActors)
	d.valueRed = ValueLeft(d.redActors)

	local blueGoal = CPos.New(RedEdge + 2, row)
	local redGoal = CPos.New(BlueEdge + 2, row)
	if spec.static then
		-- An attack order on something the side has never seen is refused, and a real attacker
		-- knows where a tower or refinery is because structures stay on the map once scouted.
		-- Vision needs a tick to land before the order is accepted.
		d.extras[#d.extras + 1] = Actor.Create("camera", true, { Owner = Blue, Location = CPos.New(RedEdge + 2, row) })
		Trigger.AfterDelay(3, function()
			if d.done then
				return
			end
			for _, a in ipairs(d.blueActors) do
				if not a.IsDead and not d.redActors[1].IsDead then
					a.Attack(d.redActors[1], true, true)
				end
			end
		end)
		return
	end

	for _, a in ipairs(d.blueActors) do
		a.AttackMove(blueGoal)
	end

	for _, a in ipairs(d.redActors) do
		a.AttackMove(redGoal)
	end
end

Clear = function(lane, why)
	local row = LaneRow(lane)
	local topLeft = Map.CenterOfCell(CPos.New(0, row - 12))
	local bottomRight = Map.CenterOfCell(CPos.New(RedEdge + Columns + 14, row + 12))
	local found = Map.ActorsInBox(topLeft, bottomRight)
	local removed = 0
	for _, a in ipairs(found) do
		if not a.IsDead then
			-- Destroy only queues a RemoveSelf activity behind whatever the actor is already doing,
			-- so a survivor still attack-moving would stay and fight the next duel in this lane.
			a.Stop()
			a.Destroy()
			removed = removed + 1
		end
	end
	print(table.concat({ "LAB", "clear", lane, why or "", #found, removed }, "|"))
end

Report = function(d, reason)
	local spec = d.spec
	local blueAlive, redAlive = Alive(d.blueActors), Alive(d.redActors)
	local winner = "none"
	if blueAlive > 0 and redAlive == 0 then
		winner = "blue"
	elseif redAlive > 0 and blueAlive == 0 then
		winner = "red"
	elseif blueAlive == 0 and redAlive == 0 then
		winner = "draw"
	end

	print(table.concat({ "DUEL", spec.id, spec.scenario, spec.a, spec.na, d.valueBlue, spec.b, spec.nb, d.valueRed,
		winner, reason, DateTime.GameTime - d.start,
		blueAlive, redAlive, ValueLeft(d.blueActors), ValueLeft(d.redActors),
		d.blue.dealt, d.red.dealt, d.blue.kills, d.red.kills,
		d.blue.firstTick or -1, d.blue.firstDistance, d.blue.maxDistance,
		d.red.firstTick or -1, d.red.firstDistance, d.red.maxDistance,
		d.friendly }, "|"))
end

Finish = function(d, reason)
	d.done = true
	local ok, err = pcall(Report, d, reason)
	if not ok then
		print("LAB|error|report|" .. tostring(d.spec.id) .. "|" .. tostring(err))
	end

	for _, a in ipairs(d.extras) do
		if not a.IsDead then
			a.Stop()
			a.Destroy()
		end
	end

	Clear(d.lane, "finish")
	Lanes[d.lane] = "cooling"
	Done = Done + 1

	local lane = d.lane
	Trigger.AfterDelay(DateTime.Seconds(2), function()
		Clear(lane, "cooled")
		Lanes[lane] = nil
	end)
end

Check = function(d)
	local now = DateTime.GameTime
	local blueAlive, redAlive = Alive(d.blueActors), Alive(d.redActors)
	if blueAlive == 0 or redAlive == 0 then
		Finish(d, "decided")
	elseif now - d.start >= TimeLimit then
		Finish(d, "timeout")
	elseif now - d.lastDamage >= QuietLimit then
		Finish(d, "stalemate")
	end
end

Loop = function()
	for lane = 1, LaneCount do
		local d = Lanes[lane]
		if d == nil then
			if Next <= #Queue then
				local spec = Queue[Next]
				Next = Next + 1
				local ok, err = pcall(Start, lane, spec)
				if not ok then
					print("LAB|error|start|" .. tostring(spec.id) .. "|" .. tostring(err))
					if type(Lanes[lane]) == "table" then
						Finish(Lanes[lane], "error")
					else
						Done = Done + 1
					end
				end
			end
		elseif d ~= "cooling" and not d.done then
			local ok, err = pcall(Check, d)
			if not ok then
				print("LAB|error|check|" .. tostring(d.spec.id) .. "|" .. tostring(err))
				Finish(d, "error")
			end
		end
	end

	if Done >= #Queue then
		print("LAB|done|" .. Done)
		Lab.MarkCompletedObjective(Objective)
		return
	end

	Trigger.AfterDelay(Interval, Loop)
end

WorldLoaded = function()
	Lab = Player.GetPlayer("Lab")
	Blue = Player.GetPlayer("Blue")
	Red = Player.GetPlayer("Red")
	Objective = Lab.AddObjective("run-duels", "Primary", true)

	-- Towers draw power and a script player owns none, so both sides get a quiet power station
	-- far outside every lane: an unpowered obelisk would lose every fight by standing still.
	for i = 0, 3 do
		Actor.Create("nuk2", true, { Owner = Blue, Location = CPos.New(64, 4 + i * 4) })
		Actor.Create("nuk2", true, { Owner = Red, Location = CPos.New(64, 150 + i * 4) })
	end

	if LabDebug then
		LabDebug()
		return
	end

	BuildQueue()
	print(table.concat({ "LAB", "queued", #Queue, "budget", Budget, "towerBudget", TowerBudget,
		"raidBudget", RaidBudget, "gap", Gap, "timeLimit", TimeLimit, "quietLimit", QuietLimit }, "|"))
	Trigger.AfterDelay(DateTime.Seconds(1), Loop)
end
