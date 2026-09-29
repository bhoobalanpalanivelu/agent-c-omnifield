define("Cases_FormPage", /**SCHEMA_DEPS*/[]/**SCHEMA_DEPS*/, function/**SCHEMA_ARGS*/()/**SCHEMA_ARGS*/ {
	return {
		viewConfigDiff: /**SCHEMA_VIEW_CONFIG_DIFF*/[
			{
				"operation": "remove",
				"name": "RequeueQueueItemButton"
			},
			{
				"operation": "remove",
				"name": "PostponeQueueItemButton"
			},
			{
				"operation": "merge",
				"name": "Tabs",
				"values": {
					"allowToggleClose": true
				}
			},
			{
				"operation": "merge",
				"name": "CardToggleTabPanel",
				"values": {
					"allowToggleClose": true
				}
			},
			{
				"operation": "insert",
				"name": "Cr8ResolutionAudit",
				"values": {
					"type": "crt.Button",
					"caption": "#ResourceString(Cr8ResolutionAudit_caption)#",
					"color": "default",
					"disabled": false,
					"size": "medium",
					"iconPosition": "left-icon",
					"visible": true,
					"clicked": {
						"request": "crt.OpenChangeLogRequest",
						"params": {
							"dataSource": "PDS",
							"entityName": null,
							"designPropertiesParams": {
								"dataSource": "PDS",
								"entityName": "Case",
								"selected": "dataSource",
								"dataSourceWasInvalid": false,
								"entityNameWasInvalid": false
							}
						}
					},
					"clickMode": "default",
					"icon": "view-eye-icon"
				},
				"parentName": "CardToggleContainer",
				"propertyName": "items",
				"index": 2
			}
		]/**SCHEMA_VIEW_CONFIG_DIFF*/,
		viewModelConfigDiff: /**SCHEMA_VIEW_MODEL_CONFIG_DIFF*/[]/**SCHEMA_VIEW_MODEL_CONFIG_DIFF*/,
		modelConfigDiff: /**SCHEMA_MODEL_CONFIG_DIFF*/[]/**SCHEMA_MODEL_CONFIG_DIFF*/,
		handlers: /**SCHEMA_HANDLERS*/[]/**SCHEMA_HANDLERS*/,
		converters: /**SCHEMA_CONVERTERS*/{}/**SCHEMA_CONVERTERS*/,
		validators: /**SCHEMA_VALIDATORS*/{}/**SCHEMA_VALIDATORS*/
	};
});