- add endpoint to get latest values of variables  for instance
- async flow asyncAfter/asyncBefore
- inhance ui to be like modern dashboard
- flow /api/instances/{id}/flows/{flowId} endpoint return a lot of data may need enhancement


- no need to role in flow after Error boundary,start event,meesage,script task,service task (done)
- find way to notify (api response) that action fail without the hole instance fail (keep status 200 and can set error on variable and the app who call the api should check the variables return in flow endpoint reponse , or check the current node return in flow endpoint reponse ) (done)
- role for start event (done)
- show node id in side panel (done)
- add message event that expose as endpoint can called from other system to make flow sequence continue (done)
