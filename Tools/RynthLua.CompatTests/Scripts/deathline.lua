-- DreamWeave's death line ("... (Killing blow: N damage. You had H/M health.)") fires OnDeath
-- (2026-11-15: a script missed one, the health read having skipped the 0 too).
local death
game.Character.OnDeath.Add(function(e) death = e end)
sleep(700)

harness.chat("You are fatally punctured by Zefir Knotbinder! (Killing blow: 130 damage. You had 21/346 health.)", ChatMessageType.System)
local t = 0
while not death and t < 3000 do sleep(20); t = t + 20 end
check(death ~= nil, "OnDeath on DreamWeave's killing-blow line")
if death then
  check(death.Text:find("Killing blow", 1, true) ~= nil, "OnDeath.Text is the line")
end

done()
