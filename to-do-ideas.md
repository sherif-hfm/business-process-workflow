- start event and message start event business keys unique constraints
- start event Idempotency 
- check conflict claim and Assignee 
- in instance list api add Node ID  and External ID to return 

- add Parallel Gateways
- async flow asyncAfter/asyncBefore
- Timers events

- inhance ui to be like modern dashboard
- add endpoint like /api/instances/{id}/flows/{flowId} but using flow External ID
- add endpoint to get latest values of variables  for instance
- add endpoint to get history of instance
- flow /api/instances/{id}/flows/{flowId} endpoint return a lot of data may need enhancement
- /api/instances/{id} endpoint return a lot of data may need enhancement

--------------------------- done ---------------------------
- parallel multi instance and sequence multi instance for user task (done)
- manage versions of workflow and change default version and published versions (done)
- enable docmentation in swagger (done)
- protect workflow endpoints (done)
- add serilog .net logging (done)
- add engine settings table  (done)
- load workflow direct from file not past data (done)
- convert "WorkflowKey" column and id in json to string and can start instance using it  (done)
- no need to role in flow after Error boundary,start event,meesage,script task,service task (done)
- find way to notify (api response) that action fail without the hole instance fail (keep status 200 and can set error on variable and the app who call the api should check the variables return in flow endpoint reponse , or check the current node return in flow endpoint reponse ) (done)
- role for start event (done)
- show node id in side panel (done)
- add message event that expose as endpoint can called from other system to make flow sequence continue (done)
