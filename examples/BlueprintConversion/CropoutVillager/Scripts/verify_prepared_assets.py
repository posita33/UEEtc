"""Read-only verification, in a separate Editor process after prepare_assets.py."""
import json
import unreal

table = unreal.load_asset("/CropoutVillager/Prototype/DT_JobsNative")
bp = unreal.load_asset("/CropoutVillager/Prototype/BP_VillagerNative")
assert table is not None and bp is not None
defaults = unreal.get_default_object(bp.generated_class())
assert defaults.get_editor_property("jobs") == table
assert defaults.get_editor_property("body").get_skeletal_mesh_asset().get_name() == "SKM_Villager"
assert defaults.get_editor_property("body").get_editor_property("anim_class").get_name() == "ABP_Villager_C"
assert defaults.get_editor_property("resource_crate").get_name() == "Crate"
assert defaults.get_editor_property("delivery_montage").get_name() == "AM_PutDown"
rows = json.loads(unreal.DataTableFunctionLibrary.export_data_table_to_json_string(table))
source = unreal.load_asset("/Game/Blueprint/Villagers/DT_Jobs")
original = json.loads(unreal.DataTableFunctionLibrary.export_data_table_to_json_string(source))
assert len(rows) == len(original) and len(rows) > 0
assert any(row["Name"] == "Idle" for row in rows)
for row in rows:
    assert "BehaviorTree" in row and "WorkAnimation" in row
unreal.log("UEETC_NATIVE_VILLAGER_VERIFIED: persisted Blueprint components and jobs=" + str(len(rows)))
