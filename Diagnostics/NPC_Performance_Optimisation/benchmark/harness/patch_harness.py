import sys
src, dst = sys.argv[1], sys.argv[2]
s = open(src, encoding="utf-8").read()
def rep(old, new):
    global s
    assert s.count(old) == 1, old[:70]
    s = s.replace(old, new)
rep("public bool noAgentPrefab = false;", "public GameObject templateOverride; public int attractorCount = 0; public bool noAgentPrefab = false;")
rep("rig.template = MakeTemplate(o.npcSpeed, o);", "rig.template = o.templateOverride != null ? o.templateOverride : MakeTemplate(o.npcSpeed, o);")
a = s.index('        if (o.attractor)\n        {\n            var attractorType = T("AttractorInfo");')
b = s.index('        Set(rig.spawner, "navmeshWaitTimeout", 1f);')
newblk = '''        if (o.attractor)
        {
            var attractorType = T("AttractorInfo");
            int ac = o.attractorCount > 0 ? o.attractorCount : 1;
            var arr = Array.CreateInstance(attractorType, ac);
            for (int ai = 0; ai < ac; ai++)
            {
                var info = Activator.CreateInstance(attractorType);
                var att = Make("Attractor_" + ai);
                float ang = ai * 6.2831853f / ac;
                att.transform.position = ac == 1 ? new Vector3(0f, 0.1f, 0f) : new Vector3(Mathf.Cos(ang) * 80f, 0.1f, Mathf.Sin(ang) * 80f);
                Set(info, "transform", att.transform);
                Set(info, "capacity", 0);
                arr.SetValue(info, ai);
            }
            Set(rig.spawner, "attractors", arr);
            Set(rig.spawner, "dwellTimeSec", o.attractorCount > 0 ? new Vector2(20f, 40f) : new Vector2(0.5f, 0.5f));
        }
'''
s = s[:a] + newblk + s[b:]
rep('Set(rig.spawner, "minAttractorsPerAgent", o.attractor ? 1 : 0);', 'Set(rig.spawner, "minAttractorsPerAgent", o.attractor ? (o.attractorCount > 0 ? o.attractorCount : 1) : 0);')
rep('Set(rig.spawner, "maxAttractorsPerAgent", o.attractor ? 1 : 0);', 'Set(rig.spawner, "maxAttractorsPerAgent", o.attractor ? (o.attractorCount > 0 ? o.attractorCount : 1) : 0);')
open(dst, "w", encoding="utf-8").write(s)
