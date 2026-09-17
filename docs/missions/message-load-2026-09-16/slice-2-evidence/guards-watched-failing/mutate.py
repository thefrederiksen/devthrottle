import subprocess, sys, os, json
# usage: mutate.py <file> <project> <filter> <mutations.json>
f, proj, flt, muts = sys.argv[1:5]
orig = open(f).read()
env = dict(os.environ, PATH=os.path.expanduser("~/.dotnet")+":"+os.environ["PATH"], DOTNET_ROOT=os.path.expanduser("~/.dotnet"))
for m in json.load(open(muts)):
    assert orig.count(m["old"]) == 1, ("not unique", m["name"], orig.count(m["old"]))
    open(f, "w").write(orig.replace(m["old"], m["new"]))
    try:
        r = subprocess.run(["dotnet", "test", proj, "--filter", flt], capture_output=True, text=True, env=env)
        out = r.stdout + r.stderr
        failed = [l.strip() for l in out.splitlines() if l.strip().startswith("Failed ")]
        summary = [l.strip() for l in out.splitlines() if "Failed!" in l or "Passed!" in l or " error " in l][:3]
        print(f"== MUTATION {m['name']}: exit={r.returncode}")
        for l in summary: print("   ", l)
        for l in failed: print("   ", l)
    finally:
        open(f, "w").write(orig)
