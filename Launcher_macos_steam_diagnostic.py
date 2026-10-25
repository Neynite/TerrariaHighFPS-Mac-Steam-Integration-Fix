import tkinter as tk
from tkinter import messagebox, ttk, filedialog
import subprocess
import os
import shutil
import threading
import sys
import json
import platform
import traceback
import datetime

WORK_DIR = os.path.dirname(os.path.abspath(__file__))
PATCHER_EXE = os.path.join(WORK_DIR, "Patcher.exe")
CONFIG_FILE = os.path.join(WORK_DIR, "config.json")

IS_WINDOWS = platform.system() == "Windows"
IS_MAC = platform.system() == "Darwin"

class TerrariaLauncher:
    def __init__(self, root, steam_command=None):
        self.root = root
        self.steam_command = steam_command
        self.root.title("Terraria High FPS Launcher")
        self.root.geometry("400x480")
        self.root.resizable(False, False)
        
        self.game_dir = self.load_game_dir()
        self.max_fps = self.load_max_fps()

        self.setup_ui()
        self.check_status()

    def load_game_dir(self):
        if IS_WINDOWS:
            default_dirs = [r"C:\Program Files (x86)\Steam\steamapps\common\Terraria"]
        elif IS_MAC:
            resources = "Terraria.app/Contents/Resources"
            default_dirs = [
                os.path.join(os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Terraria"), resources),
            ]
        else:
            default_dirs = ["/data/SteamLibrary/steamapps/common/Terraria"]

        # Try loading from config
        if os.path.exists(CONFIG_FILE):
            try:
                with open(CONFIG_FILE, 'r') as f:
                    config = json.load(f)
                    saved_dir = config.get("game_dir", "")
                    if os.path.exists(os.path.join(saved_dir, "Terraria.exe")):
                        return saved_dir
            except:
                pass

        # Try defaults
        for default_dir in default_dirs:
            if os.path.exists(os.path.join(default_dir, "Terraria.exe")):
                self.save_game_dir(default_dir)
                return default_dir

        # Ask user
        return self.prompt_for_directory()

    def resolve_mac_dir(self, selected_dir):
        """If user selects the Terraria Steam folder or .app bundle, resolve to Resources."""
        candidates = [
            selected_dir,
            os.path.join(selected_dir, "Terraria.app/Contents/Resources"),
            os.path.join(selected_dir, "Contents/Resources"),
        ]
        for path in candidates:
            if os.path.exists(os.path.join(path, "Terraria.exe")):
                return path
        return selected_dir

    def prompt_for_directory(self):
        if IS_MAC:
            msg = "Could not automatically find Terraria. Please select your 'Terraria' folder inside Steam (usually ~/Library/Application Support/Steam/steamapps/common/Terraria)."
        else:
            msg = "Could not automatically find Terraria. Please select your 'Terraria' installation folder (where Terraria.exe is located)."
        messagebox.showinfo("Select Terraria Folder", msg)
        while True:
            selected_dir = filedialog.askdirectory(title="Select Terraria Directory")
            if not selected_dir:
                sys.exit(0)

            resolved = self.resolve_mac_dir(selected_dir) if IS_MAC else selected_dir
            if os.path.exists(os.path.join(resolved, "Terraria.exe")):
                self.save_game_dir(resolved)
                return resolved
            else:
                messagebox.showerror("Error", "Terraria.exe not found in that folder. Please try again.")

    def load_max_fps(self):
        if os.path.exists(CONFIG_FILE):
            try:
                with open(CONFIG_FILE, 'r') as f:
                    return json.load(f).get("max_fps", 500)
            except:
                pass
        return 500

    def save_config(self):
        try:
            config = {}
            if os.path.exists(CONFIG_FILE):
                with open(CONFIG_FILE, 'r') as f:
                    config = json.load(f)
            config["game_dir"] = self.game_dir
            config["max_fps"] = self.max_fps
            with open(CONFIG_FILE, 'w') as f:
                json.dump(config, f)
        except:
            pass

    def save_game_dir(self, path):
        self.game_dir = path
        self.save_config()

    def change_directory(self):
        new_dir = filedialog.askdirectory(title="Select Terraria Directory")
        if not new_dir:
            return
        resolved = self.resolve_mac_dir(new_dir) if IS_MAC else new_dir
        if os.path.exists(os.path.join(resolved, "Terraria.exe")):
            self.game_dir = resolved
            self.save_game_dir(resolved)
            self.check_status()
            messagebox.showinfo("Success", "Game directory updated successfully!")
        else:
            messagebox.showerror("Error", "Terraria.exe not found in that folder.")

    def setup_ui(self):
        style = ttk.Style()
        style.configure("TButton", padding=6, font=("Helvetica", 10))
        
        main_frame = ttk.Frame(self.root, padding="20")
        main_frame.pack(fill=tk.BOTH, expand=True)

        ttk.Label(main_frame, text="Terraria 1.4.5 High FPS", font=("Helvetica", 16, "bold")).pack(pady=5)
        
        self.status_label = ttk.Label(main_frame, text="Status: Unknown", font=("Helvetica", 10))
        self.status_label.pack(pady=5)

        self.btn_launch_highfps = ttk.Button(main_frame, text="Launch High FPS (True Smooth)", command=self.launch_highfps)
        self.btn_launch_highfps.pack(fill=tk.X, pady=5)

        self.btn_launch_gpubatch = ttk.Button(main_frame, text="Launch High FPS + GPU Batch (Beta)", command=self.launch_gpubatch)
        self.btn_launch_gpubatch.pack(fill=tk.X, pady=5)

        self.btn_launch_vanilla = ttk.Button(main_frame, text="Launch Vanilla (60 FPS)", command=self.launch_vanilla)
        self.btn_launch_vanilla.pack(fill=tk.X, pady=5)

        self.btn_patch = ttk.Button(main_frame, text="Update/Create Patch", command=self.create_patch)
        self.btn_patch.pack(fill=tk.X, pady=5)

        self.btn_gpubatch_patch = ttk.Button(main_frame, text="Update/Create GPU Batch Patch", command=self.create_gpubatch_patch)
        self.btn_gpubatch_patch.pack(fill=tk.X, pady=5)

        ttk.Separator(main_frame, orient='horizontal').pack(fill=tk.X, pady=5)
        
        self.btn_change_dir = ttk.Button(main_frame, text="Change Game Directory", command=self.change_directory)
        self.btn_change_dir.pack(fill=tk.X, pady=5)

        fps_frame = ttk.Frame(main_frame)
        fps_frame.pack(fill=tk.X, pady=2)
        ttk.Label(fps_frame, text="Max FPS:").pack(side=tk.LEFT)
        self.fps_var = tk.StringVar(value=str(self.max_fps))
        fps_entry = ttk.Entry(fps_frame, textvariable=self.fps_var, width=6)
        fps_entry.pack(side=tk.LEFT, padx=5)
        ttk.Label(fps_frame, text="(0 = unlimited, default 500)", font=("Helvetica", 8)).pack(side=tk.LEFT)

        if self.steam_command:
            ttk.Label(main_frame, text="Mode: Steam Integration Active", foreground="blue").pack(pady=5)

        ttk.Label(main_frame, text="Note: Turn 'Frame Skip' OFF in game settings.", font=("Helvetica", 8, "italic")).pack(side=tk.BOTTOM, pady=5)

    def check_status(self):
        vanilla_bak = os.path.join(self.game_dir, "Terraria.exe.vanilla")
        if os.path.exists(vanilla_bak):
            self.status_label.config(text="Status: Ready (Backups exist)", foreground="green")
        else:
            self.status_label.config(text="Status: Initial Run (Will backup on first launch)", foreground="orange")

    def create_patch(self):
        def run():
            try:
                self.status_label.config(text="Status: Patching...", foreground="blue")
                target_exe = os.path.join(self.game_dir, "Terraria.exe")
                vanilla_bak = os.path.join(self.game_dir, "Terraria.exe.vanilla")
                
                # Always patch from vanilla to avoid double-patching
                if os.path.exists(vanilla_bak):
                    source_exe = vanilla_bak
                else:
                    source_exe = target_exe
                    # Make a backup if it doesn't exist
                    shutil.copy2(target_exe, vanilla_bak)

                output_exe = os.path.join(self.game_dir, "Terraria.exe.highfps")
                logic_dll_src = os.path.join(WORK_DIR, "HighFPSLogic.dll")
                logic_dll_dst = os.path.join(self.game_dir, "HighFPSLogic.dll")
                
                shutil.copy2(logic_dll_src, logic_dll_dst)
                
                cmd = [PATCHER_EXE, source_exe, output_exe, logic_dll_dst]
                if not IS_WINDOWS:
                    cmd.insert(0, "mono")
                    
                result = subprocess.run(cmd, capture_output=True, text=True)
                try:
                    with open(os.path.join(WORK_DIR, "patcher.log"), "w") as logf:
                        logf.write("=== STDOUT ===\n" + result.stdout + "\n=== STDERR ===\n" + result.stderr)
                except Exception:
                    pass

                if "Success!" in result.stdout:
                    # Copy version-fixed DLL back so the launch-time copy stays correct
                    shutil.copy2(logic_dll_dst, logic_dll_src)
                    messagebox.showinfo("Success", "High FPS patch created successfully!\n(Uses Native Accumulator Logic)")
                    self.status_label.config(text="Status: Patch Updated", foreground="green")
                else:
                    messagebox.showerror("Error", f"Patching failed:\n{result.stdout}\n{result.stderr}")
                    self.status_label.config(text="Status: Patch Failed", foreground="red")
            except Exception as e:
                messagebox.showerror("Error", str(e))

        threading.Thread(target=run).start()

    def create_gpubatch_patch(self):
        def run():
            try:
                self.status_label.config(text="Status: Creating GPU Batch Patch...", foreground="blue")
                vanilla_bak = os.path.join(self.game_dir, "Terraria.exe.vanilla")
                target_exe  = os.path.join(self.game_dir, "Terraria.exe")

                if os.path.exists(vanilla_bak):
                    source_exe = vanilla_bak
                else:
                    source_exe = target_exe
                    shutil.copy2(target_exe, vanilla_bak)

                output_exe       = os.path.join(self.game_dir, "Terraria.exe.gpubatch")
                logic_dll_src    = os.path.join(WORK_DIR, "GpuBatchLogic.dll")
                logic_dll_dst    = os.path.join(self.game_dir, "GpuBatchLogic.dll")

                if not os.path.exists(logic_dll_src):
                    messagebox.showerror("Error", "GpuBatchLogic.dll not found.\nBuild it first with mcs.")
                    self.status_label.config(text="Status: GPU Batch DLL Missing", foreground="red")
                    return

                shutil.copy2(logic_dll_src, logic_dll_dst)

                cmd = [PATCHER_EXE, source_exe, output_exe, logic_dll_dst, "--gpu-batch"]
                if not IS_WINDOWS:
                    cmd.insert(0, "mono")

                result = subprocess.run(cmd, capture_output=True, text=True)

                if "Success!" in result.stdout:
                    # Copy version-fixed DLL back so the launch-time copy stays correct
                    shutil.copy2(logic_dll_dst, logic_dll_src)
                    messagebox.showinfo("Success", "GPU Batch patch created successfully!")
                    self.status_label.config(text="Status: GPU Batch Patch Updated", foreground="green")
                else:
                    messagebox.showerror("Error", f"GPU Batch patching failed:\n{result.stdout}\n{result.stderr}")
                    self.status_label.config(text="Status: GPU Batch Patch Failed", foreground="red")
            except Exception as e:
                messagebox.showerror("Error", str(e))

        threading.Thread(target=run).start()

    def launch_vanilla(self):
        self.launch(use_highfps=False)

    def launch_highfps(self):
        self.launch(use_highfps=True)

    def launch_gpubatch(self):
        self.launch(use_highfps=True, use_gpubatch=True)

    def launch(self, use_highfps, use_gpubatch=False):
        exe_path    = os.path.join(self.game_dir, "Terraria.exe")
        vanilla_bak = os.path.join(self.game_dir, "Terraria.exe.vanilla")
        highfps_exe = os.path.join(self.game_dir, "Terraria.exe.highfps")
        gpubatch_exe = os.path.join(self.game_dir, "Terraria.exe.gpubatch")
        fna_path    = os.path.join(self.game_dir, "FNA.dll")
        fna_vanilla = os.path.join(self.game_dir, "FNA.dll.vanilla")
        fna_gpubatch = os.path.join(self.game_dir, "FNA.dll.gpubatch")

        # 1. Backup if needed
        if not os.path.exists(vanilla_bak):
            shutil.copy2(exe_path, vanilla_bak)
        if use_gpubatch and os.path.exists(fna_path) and not os.path.exists(fna_vanilla):
            shutil.copy2(fna_path, fna_vanilla)

        # 2. Prepare EXE + FNA.dll
        if use_gpubatch:
            if not os.path.exists(gpubatch_exe) or not os.path.exists(fna_gpubatch):
                self.create_gpubatch_patch()
                return
            shutil.copy2(gpubatch_exe, exe_path)
            shutil.copy2(fna_gpubatch, fna_path)
        elif use_highfps:
            if not os.path.exists(highfps_exe):
                self.create_patch()
                return
            shutil.copy2(highfps_exe, exe_path)
            if os.path.exists(fna_vanilla):
                shutil.copy2(fna_vanilla, fna_path)  # restore vanilla FNA for non-gpu mode
        else:
            shutil.copy2(vanilla_bak, exe_path)
            if os.path.exists(fna_vanilla):
                shutil.copy2(fna_vanilla, fna_path)  # restore vanilla FNA

        # 3. Launch
        try:
            self.max_fps = int(self.fps_var.get())
        except ValueError:
            self.max_fps = 500
        self.save_config()

        launch_env = os.environ.copy()
        if use_highfps:
            launch_env["TERRARIA_MAX_FPS"] = str(self.max_fps)

        self.status_label.config(text="Status: Game Running...", foreground="blue")
        try:
            if self.steam_command:
                # Steam Mode: Execute the command Steam gave us
                print("DEBUG steam_command:", self.steam_command)
                subprocess.Popen(self.steam_command, cwd=self.game_dir, env=launch_env)
            else:
                # CLI Mode: Run the standard launcher
                if IS_WINDOWS:
                    subprocess.Popen([exe_path], cwd=self.game_dir, env=launch_env)
                elif IS_MAC:
                    mac_launcher = os.path.normpath(os.path.join(self.game_dir, "..", "MacOS", "Terraria"))
                    subprocess.Popen([mac_launcher], cwd=self.game_dir, env=launch_env)
                else:
                    subprocess.Popen([os.path.join(self.game_dir, "Terraria")], cwd=self.game_dir, env=launch_env)
            
            # Close launcher after launching game to avoid confusion
            self.root.destroy()
        except Exception as e:
            messagebox.showerror("Launch Error", str(e))


def steam_autolaunch(steam_command):
    """Prepare the High FPS build, then replace this process with Steam's game command."""
    log_path = os.path.join(WORK_DIR, "steam_launch.log")
    def log(message):
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(f"[{datetime.datetime.now().isoformat()}] {message}\n")

    with open(log_path, "w", encoding="utf-8") as f:
        f.write("Terraria High FPS Steam launch diagnostics\n")
    log(f"Python: {sys.executable}")
    log(f"Platform: {platform.platform()}")
    log(f"WORK_DIR: {WORK_DIR}")
    log(f"Steam command args: {steam_command!r}")
    log(f"PATH: {os.environ.get('PATH', '')}")
    if not steam_command:
        raise RuntimeError("Steam did not pass a game command. Check the Launch Options.")

    config = {}
    try:
        with open(CONFIG_FILE, "r") as f:
            config = json.load(f)
    except (OSError, json.JSONDecodeError):
        pass

    game_dir = config.get("game_dir", "")
    log(f"Configured game_dir: {game_dir!r}")
    if not game_dir or not os.path.isfile(os.path.join(game_dir, "Terraria.exe")):
        if IS_MAC:
            game_dir = os.path.join(
                os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Terraria"),
                "Terraria.app", "Contents", "Resources"
            )
        else:
            raise RuntimeError(
                "Terraria's game directory is not configured. Open Launcher.py normally once "
                "and use 'Change Game Directory' to select Terraria.app/Contents/Resources."
            )

    log(f"Resolved game_dir: {game_dir!r}")
    exe_path = os.path.join(game_dir, "Terraria.exe")
    log(f"Terraria.exe exists: {os.path.isfile(exe_path)}")
    log(f"Patcher.exe exists: {os.path.isfile(PATCHER_EXE)}")
    vanilla_bak = os.path.join(game_dir, "Terraria.exe.vanilla")
    highfps_exe = os.path.join(game_dir, "Terraria.exe.highfps")
    logic_dll_src = os.path.join(WORK_DIR, "HighFPSLogic.dll")
    logic_dll_dst = os.path.join(game_dir, "HighFPSLogic.dll")

    if not os.path.isfile(vanilla_bak):
        shutil.copy2(exe_path, vanilla_bak)

    # Create the patched assembly on first use, or whenever it is missing.
    if not os.path.isfile(highfps_exe):
        if not os.path.isfile(PATCHER_EXE):
            raise RuntimeError(f"Patcher.exe not found: {PATCHER_EXE}")
        if not os.path.isfile(logic_dll_src):
            raise RuntimeError(f"HighFPSLogic.dll not found: {logic_dll_src}")

        shutil.copy2(logic_dll_src, logic_dll_dst)
        cmd = ["mono", PATCHER_EXE, vanilla_bak, highfps_exe, logic_dll_dst]
        log(f"Running patcher command: {cmd!r}")
        result = subprocess.run(cmd, cwd=WORK_DIR, capture_output=True, text=True)
        log(f"Patcher return code: {result.returncode}")
        try:
            with open(os.path.join(WORK_DIR, "patcher.log"), "w") as logf:
                logf.write("=== STDOUT ===\n" + result.stdout +
                           "\n=== STDERR ===\n" + result.stderr)
        except OSError:
            pass

        if result.returncode != 0 or "Success!" not in result.stdout or not os.path.isfile(highfps_exe):
            raise RuntimeError(
                "Could not create the High FPS patch.\n\n"
                f"stdout:\n{result.stdout}\n\nstderr:\n{result.stderr}\n\n"
                "Check that Mono is installed and that Patcher.exe and HighFPSLogic.dll "
                "are present beside Launcher.py."
            )
        shutil.copy2(logic_dll_dst, logic_dll_src)

    # Make Steam's normal launch command load the patched managed assembly.
    shutil.copy2(highfps_exe, exe_path)
    launch_env = os.environ.copy()
    launch_env["TERRARIA_MAX_FPS"] = str(config.get("max_fps", 500))

    # Replace this Python process rather than leaving a wrapper parent process behind.
    os.chdir(game_dir)
    log(f"Launching Steam command: {steam_command!r}")
    log(f"Launch cwd: {os.getcwd()!r}")
    os.execvpe(steam_command[0], steam_command, launch_env)


if __name__ == "__main__":
    # Steam launch options should be:
    # /usr/bin/python3 "/absolute/path/to/Launcher.py" --steam-launch %command%
    if len(sys.argv) > 1 and sys.argv[1] == "--steam-launch":
        try:
            steam_autolaunch(sys.argv[2:])
        except Exception as exc:
            try:
                with open(os.path.join(WORK_DIR, "steam_launch.log"), "a", encoding="utf-8") as f:
                    f.write("\nFATAL ERROR: " + repr(exc) + "\n")
                    f.write(traceback.format_exc() + "\n")
            except Exception:
                pass
            print(f"Terraria High FPS Steam launch failed: {exc}", file=sys.stderr)
            sys.exit(1)
    else:
        root = tk.Tk()
        app = TerrariaLauncher(root)
        root.mainloop()
