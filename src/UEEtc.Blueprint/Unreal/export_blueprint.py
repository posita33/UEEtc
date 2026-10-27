"""Read-only export. Paths travel through a JSON request, never generated Python expressions."""
import json
import os
from pathlib import Path
import unreal

request = json.loads(Path(os.environ["UEETC_BLUEPRINT_REQUEST"]).read_text(encoding="utf-8-sig"))
output = Path(request["WorkDirectory"])
asset = unreal.load_asset(request["PackagePath"])
if asset is None or not isinstance(asset, unreal.Blueprint):
    raise RuntimeError("Not a loadable editor Blueprint: " + request["PackagePath"])
if asset.get_path_name() != request["PackagePath"] + "." + asset.get_name():
    raise RuntimeError("Asset redirects to another package; select the target .uasset explicitly.")


def export_object(obj, name):
    task = unreal.AssetExportTask()
    task.object = obj
    task.filename = str(output / (name + ".copy"))
    task.exporter = unreal.ObjectExporterT3D()
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    if not unreal.Exporter.run_asset_export_task(task):
        raise RuntimeError("Export failed: " + name + " " + str(task.errors))


export_object(asset, "Blueprint")
generated = asset.generated_class()
if generated is None:
    raise RuntimeError("Generated class is unavailable; check Blueprint and native module loading.")
export_object(generated, "Class")
export_object(unreal.get_default_object(generated), "Defaults")
(output / "metadata.json").write_text(json.dumps({
    "AssetPath": asset.get_path_name(),
    "EngineVersion": unreal.SystemLibrary.get_engine_version(),
}, ensure_ascii=False), encoding="utf-8")
# Do not compile, save, rename, or modify the source asset/project.
