using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.IO;

class FallbackResolver : DefaultAssemblyResolver
{
    readonly Dictionary<string, AssemblyDefinition> _bySimpleName = new Dictionary<string, AssemblyDefinition>();

    public void PreloadDirectory(string dir)
    {
        AddSearchDirectory(dir);
        foreach (string f in Directory.GetFiles(dir, "*.dll"))
        {
            try {
                // InMemory=true prevents holding the file handle. Without this, the resolver
                // locks every DLL in the directory — including HighFPSLogic.dll — and we can't
                // rewrite them later.
                var asm = AssemblyDefinition.ReadAssembly(f, new ReaderParameters { InMemory = true });
                RegisterAssembly(asm);
                _bySimpleName[asm.Name.Name] = asm;
                if (asm.Name.Name == "FNA")
                    Console.WriteLine("  [resolver] preloaded FNA: " + asm.Name.FullName);
            } catch (Exception ex) {
                if (Path.GetFileNameWithoutExtension(f).Equals("FNA", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("  [resolver] FAILED to preload FNA.dll: " + ex.Message);
            }
        }
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        try { return base.Resolve(name, parameters); }
        catch (AssemblyResolutionException) { }
        if (_bySimpleName.TryGetValue(name.Name, out var asm)) return asm;
        return null;
    }
}

public class HighFPSPatcher
{
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[1] == "--list-calls") {
            ListDrawCalls(args[0]);
            return;
        }

        if (args.Length < 3) {
            Console.WriteLine("Usage: HighFPSPatcher <input_exe> <output_exe> <logic_dll> [--gpu-batch]");
            Console.WriteLine("       HighFPSPatcher <input_exe> --list-calls");
            return;
        }

        string inputPath   = args[0];
        string outputPath  = args[1];
        string logicDllPath = args[2];
        bool   gpuMode     = args.Length >= 4 && args[3] == "--gpu-batch";

        var resolver = new FallbackResolver();
        resolver.PreloadDirectory(Path.GetDirectoryName(inputPath));

        ReaderParameters readerParams = new ReaderParameters { AssemblyResolver = resolver };

        try {
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams))
            {
                ModuleDefinition module = assembly.MainModule;

                // Always load the logic DLL (HighFPSLogic.cs + optionally GpuBatcher.cs)
                // InMemory=true releases the file handle so we can rewrite the DLL in place.
                AssemblyDefinition logicAssembly = AssemblyDefinition.ReadAssembly(
                    logicDllPath, new ReaderParameters { InMemory = true });

                // Fix FNA version reference in the logic DLL to match this platform's FNA.
                // HighFPSLogic.dll is compiled on Linux against FNA 26.x; Windows ships a
                // different FNA version. Mismatched references cause FileNotFoundException at runtime.
                var targetFnaRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "FNA");
                var targetXnaCore = module.AssemblyReferences.FirstOrDefault(r => r.Name == "Microsoft.Xna.Framework");
                var targetXnaGame = module.AssemblyReferences.FirstOrDefault(r => r.Name == "Microsoft.Xna.Framework.Game");
                var targetXnaGfx  = module.AssemblyReferences.FirstOrDefault(r => r.Name == "Microsoft.Xna.Framework.Graphics");
                var logicFnaRef   = logicAssembly.MainModule.AssemblyReferences.FirstOrDefault(r => r.Name == "FNA");
                Console.WriteLine("Target FNA ref:       " + (targetFnaRef  != null ? targetFnaRef.FullName  : "<none>"));
                Console.WriteLine("Target XNA core ref:  " + (targetXnaCore != null ? targetXnaCore.FullName : "<none>"));
                Console.WriteLine("Target XNA gfx ref:   " + (targetXnaGfx  != null ? targetXnaGfx.FullName  : "<none>"));
                Console.WriteLine("Logic  FNA ref:       " + (logicFnaRef   != null ? logicFnaRef.FullName   : "<none>"));

                bool logicChanged = false;
                if (logicFnaRef != null && targetFnaRef != null)
                {
                    // FNA <-> FNA: normalize version, public key token, culture
                    if (logicFnaRef.Version != targetFnaRef.Version)
                    { logicFnaRef.Version = targetFnaRef.Version; logicChanged = true; }
                    var tgtPk = targetFnaRef.PublicKeyToken ?? new byte[0];
                    var lgcPk = logicFnaRef.PublicKeyToken  ?? new byte[0];
                    if (!tgtPk.SequenceEqual(lgcPk))
                    { logicFnaRef.PublicKeyToken = targetFnaRef.PublicKeyToken; logicChanged = true; }
                    if (logicFnaRef.Culture != targetFnaRef.Culture)
                    { logicFnaRef.Culture = targetFnaRef.Culture; logicChanged = true; }
                    if (logicChanged) Console.WriteLine("Normalized FNA ref -> " + logicFnaRef.FullName);
                    else              Console.WriteLine("FNA reference already matches; no fix needed.");
                }
                else if (logicFnaRef != null && targetFnaRef == null && targetXnaCore != null)
                {
                    // Logic built against FNA, target uses XNA: redirect FNA type refs to split XNA assemblies.
                    Console.WriteLine("Redirecting FNA type references to XNA...");
                    AssemblyNameReference newCore = new AssemblyNameReference(targetXnaCore.Name, targetXnaCore.Version)
                        { PublicKeyToken = targetXnaCore.PublicKeyToken, Culture = targetXnaCore.Culture };
                    AssemblyNameReference newGame = targetXnaGame != null
                        ? new AssemblyNameReference(targetXnaGame.Name, targetXnaGame.Version)
                            { PublicKeyToken = targetXnaGame.PublicKeyToken, Culture = targetXnaGame.Culture }
                        : null;
                    AssemblyNameReference newGfx = targetXnaGfx != null
                        ? new AssemblyNameReference(targetXnaGfx.Name, targetXnaGfx.Version)
                            { PublicKeyToken = targetXnaGfx.PublicKeyToken, Culture = targetXnaGfx.Culture }
                        : null;

                    logicAssembly.MainModule.AssemblyReferences.Add(newCore);
                    if (newGame != null) logicAssembly.MainModule.AssemblyReferences.Add(newGame);
                    if (newGfx  != null) logicAssembly.MainModule.AssemblyReferences.Add(newGfx);

                    int redirected = 0;
                    foreach (var tref in logicAssembly.MainModule.GetTypeReferences())
                    {
                        if (tref.Scope != logicFnaRef) continue;
                        string ns = tref.Namespace ?? "";
                        AssemblyNameReference dest;
                        if (ns.StartsWith("Microsoft.Xna.Framework.Graphics") && newGfx != null) dest = newGfx;
                        else if (ns == "Microsoft.Xna.Framework" && (tref.Name == "Game" || tref.Name == "GameComponent" || tref.Name == "DrawableGameComponent" || tref.Name == "GameWindow") && newGame != null) dest = newGame;
                        else dest = newCore;
                        tref.Scope = dest;
                        redirected++;
                    }
                    Console.WriteLine("Redirected " + redirected + " type reference(s) FNA -> XNA");

                    logicAssembly.MainModule.AssemblyReferences.Remove(logicFnaRef);
                    logicChanged = true;
                }

                if (logicChanged) {
                    string tmp = logicDllPath + ".tmp";
                    logicAssembly.Write(tmp);
                    logicAssembly.Dispose();
                    // Replace original with the rewritten copy. Retry briefly in case AV / Explorer
                    // momentarily holds the original open.
                    Exception lastErr = null;
                    for (int attempt = 0; attempt < 10; attempt++) {
                        try { File.Delete(logicDllPath); File.Move(tmp, logicDllPath); lastErr = null; break; }
                        catch (Exception ex) { lastErr = ex; System.Threading.Thread.Sleep(200); }
                    }
                    if (lastErr != null) throw lastErr;
                    Console.WriteLine("Wrote rewritten logic DLL.");
                }
                TypeDefinition fpsManagerType    = logicAssembly.MainModule.Types.First(t => t.Name == "FPSManager");

                MethodReference preDrawMethod     = module.ImportReference(fpsManagerType.Methods.First(m => m.Name == "PreDraw"));
                MethodReference postDrawMethod    = module.ImportReference(fpsManagerType.Methods.First(m => m.Name == "PostDraw"));
                MethodReference updateMouseMethod = module.ImportReference(fpsManagerType.Methods.First(m => m.Name == "UpdateMouse"));

                TypeDefinition mainType = module.Types.First(t => t.FullName == "Terraria.Main");

                // ── Patch DoDraw: PreDraw at start, PostDraw before every ret ──
                Console.WriteLine("Patching Main.DoDraw...");
                MethodDefinition doDrawMethod = mainType.Methods.First(m => m.Name == "DoDraw" && m.Parameters.Count == 1);
                {
                    var il    = doDrawMethod.Body.GetILProcessor();
                    var first = doDrawMethod.Body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Call, preDrawMethod));

                    foreach (var instr in doDrawMethod.Body.Instructions.ToList()) {
                        if (instr.OpCode == OpCodes.Ret)
                            il.InsertBefore(instr, il.Create(OpCodes.Call, postDrawMethod));
                    }
                }

                // ── Patch Update: inject UpdateMouse() before DoUpdate call ──
                Console.WriteLine("Patching Main.Update...");
                MethodDefinition updateMethod = mainType.Methods.First(m => m.Name == "Update" && m.Parameters.Count == 1);
                {
                    var il          = updateMethod.Body.GetILProcessor();
                    var doUpdateCall = updateMethod.Body.Instructions
                        .First(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                    && i.Operand.ToString().Contains("DoUpdate"));
                    var insertPoint = doUpdateCall;
                    while (insertPoint.Previous != null &&
                           insertPoint.Previous.OpCode != OpCodes.Call &&
                           insertPoint.Previous.OpCode != OpCodes.Callvirt)
                        insertPoint = insertPoint.Previous;
                    il.InsertBefore(insertPoint, il.Create(OpCodes.Call, updateMouseMethod));
                }

                // ── GPU batch mode: inject BeginCapture / FlushCapture around DrawProjectiles ──
                if (gpuMode)
                {
                    Console.WriteLine("GPU batch mode: patching DoDraw for projectile batching...");

                    TypeDefinition gpuType    = logicAssembly.MainModule.Types.First(t => t.Name == "GpuBatchManager");
                    MethodReference beginRef  = module.ImportReference(gpuType.Methods.First(m => m.Name == "BeginCapture"));
                    MethodReference flushRef  = module.ImportReference(gpuType.Methods.First(m => m.Name == "FlushCapture"));

                    FieldDefinition sbField  = mainType.Fields.First(f => f.Name == "spriteBatch" && f.IsStatic);
                    FieldDefinition gfxField = mainType.Fields.First(f => f.Name == "graphics"    && f.IsStatic);
                    FieldReference  sbRef    = module.ImportReference(sbField);
                    FieldReference  gfxRef   = module.ImportReference(gfxField);

                    var il = doDrawMethod.Body.GetILProcessor();

                    // Find the DrawProjectiles() callvirt in DoDraw
                    var drawProjCall = doDrawMethod.Body.Instructions
                        .First(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                    && i.Operand.ToString().Contains("DrawProjectiles"));

                    // Walk back to find the ldarg.0 that loads 'this' before DrawProjectiles
                    var insertBefore = drawProjCall;
                    while (insertBefore.Previous != null &&
                           insertBefore.Previous.OpCode != OpCodes.Call &&
                           insertBefore.Previous.OpCode != OpCodes.Callvirt)
                        insertBefore = insertBefore.Previous;

                    // Insert BeginCapture() before the this-load
                    il.InsertBefore(insertBefore, il.Create(OpCodes.Call, beginRef));

                    // After DrawProjectiles(), push spriteBatch + graphics, call FlushCapture
                    // drawProjCall may have shifted — re-find it
                    drawProjCall = doDrawMethod.Body.Instructions
                        .First(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                    && i.Operand.ToString().Contains("DrawProjectiles"));
                    var afterProj = drawProjCall.Next;
                    il.InsertBefore(afterProj, il.Create(OpCodes.Ldsfld, sbRef));
                    il.InsertBefore(afterProj, il.Create(OpCodes.Ldsfld, gfxRef));
                    il.InsertBefore(afterProj, il.Create(OpCodes.Call,   flushRef));

                    // ── Wrap tile / wall drawing methods ──
                    Console.WriteLine("GPU batch mode: wrapping tile drawing methods...");
                    string[] tileCallNames = { "DoDraw_WallsTilesNPCs", "DrawSuperSpecialProjectiles" };
                    foreach (string tcName in tileCallNames)
                    {
                        var tileCall = doDrawMethod.Body.Instructions
                            .FirstOrDefault(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                                  && i.Operand.ToString().Contains(tcName));
                        if (tileCall == null) { Console.WriteLine("  " + tcName + ": not found, skipping"); continue; }

                        var tileInsert = tileCall;
                        while (tileInsert.Previous != null &&
                               tileInsert.Previous.OpCode != OpCodes.Call &&
                               tileInsert.Previous.OpCode != OpCodes.Callvirt)
                            tileInsert = tileInsert.Previous;

                        il.InsertBefore(tileInsert, il.Create(OpCodes.Call, beginRef));

                        tileCall = doDrawMethod.Body.Instructions
                            .First(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                         && i.Operand.ToString().Contains(tcName));
                        var afterTile = tileCall.Next;
                        il.InsertBefore(afterTile, il.Create(OpCodes.Ldsfld, sbRef));
                        il.InsertBefore(afterTile, il.Create(OpCodes.Ldsfld, gfxRef));
                        il.InsertBefore(afterTile, il.Create(OpCodes.Call,   flushRef));
                        Console.WriteLine("  " + tcName + ": wrapped OK");
                    }

                    // ── Off-screen AddLight skip ──
                    Console.WriteLine("Patching Lighting.AddLight (off-screen skip)...");
                    MethodReference skipLightRef = module.ImportReference(
                        gpuType.Methods.First(m => m.Name == "ShouldSkipLight"));
                    TypeDefinition lightingType = module.Types.FirstOrDefault(t => t.FullName == "Terraria.Lighting");
                    if (lightingType != null)
                    {
                        int lightPatched = 0;
                        foreach (var addLightM in lightingType.Methods.Where(m =>
                            m.Name == "AddLight" && m.IsStatic &&
                            m.Parameters.Count >= 2 &&
                            m.Parameters[0].ParameterType.FullName == "System.Int32" &&
                            m.Parameters[1].ParameterType.FullName == "System.Int32"))
                        {
                            var ilL    = addLightM.Body.GetILProcessor();
                            var firstL = addLightM.Body.Instructions[0];
                            var brF    = ilL.Create(OpCodes.Brfalse_S, firstL);
                            ilL.InsertBefore(firstL, ilL.Create(OpCodes.Ldarg_0));
                            ilL.InsertBefore(firstL, ilL.Create(OpCodes.Ldarg_1));
                            ilL.InsertBefore(firstL, ilL.Create(OpCodes.Call, skipLightRef));
                            ilL.InsertBefore(firstL, brF);
                            ilL.InsertBefore(firstL, ilL.Create(OpCodes.Ret));
                            lightPatched++;
                        }
                        Console.WriteLine("  AddLight: " + lightPatched + " overload(s) patched");
                    }
                    else Console.WriteLine("  Terraria.Lighting not found, skipping");
                }

                // Clear parameter default values whose types can't be resolved.
                // Cecil calls CheckedResolve() on these during Write() which crashes on Windows
                // when the assembly resolver can't find FNA. The defaults are compile-time
                // metadata only — the JIT never reads them at runtime.
                foreach (var t in module.GetTypes())
                    foreach (var m in t.Methods)
                        foreach (var p in m.Parameters)
                            if (p.HasConstant && p.ParameterType.Resolve() == null)
                            { p.HasConstant = false; p.Constant = null; }

                Console.WriteLine("Saving Terraria patch to " + outputPath);
                assembly.Write(outputPath);
            }

            // ── GPU batch mode: also patch FNA.dll ──
            if (gpuMode)
            {
                string fnaInput  = Path.Combine(Path.GetDirectoryName(inputPath), "FNA.dll");
                string fnaOutput = Path.Combine(Path.GetDirectoryName(outputPath), "FNA.dll.gpubatch");
                PatchFNA(fnaInput, fnaOutput, logicDllPath);
            }

            Console.WriteLine("Success! (Native Accumulator Mode)");
        } catch (Exception e) {
            Console.WriteLine("Error during patching: " + e.Message);
            Console.WriteLine(e.StackTrace);
        }
    }

    static void ListDrawCalls(string exePath)
    {
        var resolver = new FallbackResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(exePath));
        using (var asm = AssemblyDefinition.ReadAssembly(exePath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var mainType = asm.MainModule.Types.First(t => t.FullName == "Terraria.Main");
            var doDraw   = mainType.Methods.First(m => m.Name == "DoDraw" && m.Parameters.Count == 1);
            Console.WriteLine("Calls/callvirts in Main.DoDraw:");
            foreach (var instr in doDraw.Body.Instructions)
                if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                    Console.WriteLine("  " + instr.Operand);
        }
    }

    static void PatchFNA(string fnaInput, string fnaOutput, string logicDllPath)
    {
        Console.WriteLine("Patching FNA.dll -> " + fnaOutput);

        var resolver = new FallbackResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(fnaInput));
        var rp = new ReaderParameters { AssemblyResolver = resolver };

        using (var fnaAssembly = AssemblyDefinition.ReadAssembly(fnaInput, rp))
        {
            ModuleDefinition fnaModule = fnaAssembly.MainModule;

            // Load GpuBatchManager.TryRecord from the logic DLL
            AssemblyDefinition logicAssembly = AssemblyDefinition.ReadAssembly(logicDllPath);
            TypeDefinition     gpuType       = logicAssembly.MainModule.Types.First(t => t.Name == "GpuBatchManager");
            MethodDefinition   tryRecordDef  = gpuType.Methods.First(m => m.Name == "TryRecord");
            MethodReference    tryRecordRef  = fnaModule.ImportReference(tryRecordDef);

            // Find SpriteBatch.PushSprite (16 parameters)
            TypeDefinition spriteBatchType = fnaModule.Types
                .First(t => t.FullName == "Microsoft.Xna.Framework.Graphics.SpriteBatch");
            MethodDefinition pushSprite = spriteBatchType.Methods
                .First(m => m.Name == "PushSprite" && m.Parameters.Count == 16);

            Console.WriteLine("Injecting TryRecord check into SpriteBatch.PushSprite...");

            var il            = pushSprite.Body.GetILProcessor();
            var originalFirst = pushSprite.Body.Instructions[0];

            // Build and insert in forward order (each InsertBefore(originalFirst) appends
            // just before originalFirst, so forward insertion preserves forward order).
            //
            // Desired sequence:
            //   ldarg.1 (texture) .. ldarg.s 16 (effects)
            //   call bool GpuBatchManager::TryRecord(...)
            //   brfalse.s <originalFirst>
            //   ret
            //
            var brFalseInstr = il.Create(OpCodes.Brfalse_S, originalFirst);
            var retInstr     = il.Create(OpCodes.Ret);

            // arg 1 = texture (ldarg.1)
            il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_1));
            // arg 2 = srcX (ldarg.2)
            il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_2));
            // arg 3 = srcY (ldarg.3)
            il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_3));
            // args 4..16 = srcW..effects (ldarg.s N, parameters are 0-based in Cecil, not counting this)
            for (int i = 3; i < 16; i++)
                il.InsertBefore(originalFirst, il.Create(OpCodes.Ldarg_S, pushSprite.Parameters[i]));
            il.InsertBefore(originalFirst, il.Create(OpCodes.Call,      tryRecordRef));
            il.InsertBefore(originalFirst, brFalseInstr);
            il.InsertBefore(originalFirst, retInstr);

            fnaAssembly.Write(fnaOutput);
        }
        Console.WriteLine("FNA.dll patched OK.");
    }
}
