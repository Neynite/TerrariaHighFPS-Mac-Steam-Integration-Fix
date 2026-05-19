using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.IO;

class FallbackResolver : DefaultAssemblyResolver
{
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        try { return base.Resolve(name, parameters); }
        catch (AssemblyResolutionException) { return null; }
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
        resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath));

        ReaderParameters readerParams = new ReaderParameters { AssemblyResolver = resolver };

        try {
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams))
            {
                ModuleDefinition module = assembly.MainModule;

                // Always load the logic DLL (HighFPSLogic.cs + optionally GpuBatcher.cs)
                AssemblyDefinition logicAssembly = AssemblyDefinition.ReadAssembly(logicDllPath);
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
