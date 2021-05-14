# AzTinyCopier


``` bash
SourceRegion="southcentralus"
SourceRG="testrg"
SourceAcct="testacct"

DestRegion="southcentralus"
DestRG="testrg"
DestAcct="testacct"

OpsRegion="southcentralus"
OpsRG="testrg"
OpsAcct="testacct"

# the name you want to use for your Azure Container Registry
ACR="testacr"

# the name you want to use for your Application Insights Instance
AI="testai"

# the name you want to use for the ACI instance
ACI="testaci"

# Create the Resource Groups (if needed)
az group create -n $SourceRG -l $SourceRegion
az group create -n $DestRG -l $DestRegion
az group create -n $OpsRG -l $SourceRegion

# Create Storage Account (if ndded)
az storage account create --name $SourceAcct --access-tier Hot --kind StorageV2 --sku Standard_LRS --https-only true -g $SourceRG -l $SourceRegion
az storage account create --name $DestAcct --access-tier Hot --kind StorageV2 --sku Standard_LRS --https-only true -g $DestRG -l $DestRegion
az storage account create --name $OpsAcct --access-tier Hot --kind StorageV2 --sku Standard_LRS --https-only true -g $OpsRG -l $OpsRegion

# Create Container Registry
az acr create --name $ACR --admin-enabled true --sku Standard -g $OpsRG -l $OpsRegion

# Create Application Insights
#   If you don't have the AI extension installed in your shell you will be prompted to install it when you run this command
az monitor app-insights component create --app $AI -g $OpsRG -l $OpsRegion

# Package the sample into a docker container and publish it to ACR
az acr build -r $ACR https://github.com/shawnweisfeld/AzTinyCopier.git -f AzTinyCopier/Dockerfile --image aztinycopier:latest

# Request authentication information from container registry
ACRSVR="$(az acr show --name $ACR --query loginServer -o tsv)"
ACRUSER="$(az acr credential show --name $ACR --query username  -o tsv)"
ACRPWD="$(az acr credential show --name $ACR --query passwords[0].value -o tsv)"

# Request authentication information from application insights
AIKEY="$(az monitor app-insights component show --app $AI --query instrumentationKey -g $OpsRG -o tsv)"

# Request authentication information from storage account
SourceAcctCS="$(az storage account show-connection-string --name $SourceAcct -g $SourceRG -o tsv)"
DestAcctCS="$(az storage account show-connection-string --name $DestAcct -g $DestRG -o tsv)"
OpsAcctCS="$(az storage account show-connection-string --name $OpsAcct -g $OpsRG -o tsv)"

# Deploy & Run an instance of the sample to ACI for each storage container
for i in {1..10}; do
  az container create \
      --name $ACI$i \
      --resource-group $OpsRG \
      --location $OpsRegion \
      --cpu 1 \
      --memory 1 \
      --registry-login-server $ACRSVR \
      --registry-username $ACRUSER \
      --registry-password $ACRPWD \
      --image "$ACRSVR/aztinycopier:latest" \
      --restart-policy Always \
      --no-wait \
      --environment-variables \
          APPINSIGHTS_INSTRUMENTATIONKEY=$AIKEY \
          SourceConnection=$SourceAcctCS \
          DestinationConnection=$DestAcctCS \
          OperationConnection=$OpsAcctCS \
          QueueName="myqueue" \
          WhatIf="false" \
          VisibilityTimeout="60" \
          SleepWait="1" \
          ThreadCount="0" \
          Delimiter="/"
done
```

Queue message to process the entire storage account from the root.
```
{ "Action": "ProcessAccount" }
```

Queue message to start processing the storage account at an arbitrary path (including all subfolders). NOTE: make sure to include the trailing delimiter (i.e. the `/`)
```
{"Action":"ProcessPath","Container":"mycontainer","Path":"myfolder/myfolder/"}
```


```

dependencies 
| where target == 'ProcessPath' and timestamp > datetime(2021-05-12 12:00:00)
| project timestamp,
    Container=customDimensions['Container'],
    Prefix=customDimensions['Prefix'],
    Delimiter=customDimensions['Delimiter'],
    DestinationConnection=customDimensions['DestinationConnection'],
    Run=customDimensions['Run'],
    SourceConnection=customDimensions['SourceConnection'],
    ThreadCount=customDimensions['ThreadCount'],
    WhatIf=customDimensions['WhatIf'],
    blobBytes=tolong(customDimensions['blobBytes']),
    blobBytesMoved=tolong(customDimensions['blobBytesMoved']),
    blobCount=tolong(customDimensions['blobCount']),
    blobCountMoved=tolong(customDimensions['blobCountMoved']),
    subPrefixes=tolong(customDimensions['subPrefixes']),
    durationMin=round(duration/60000, 2),
    blobsPerMin=round(tolong(customDimensions['blobCountMoved'])/(duration/60000), 2)
| order by timestamp desc

```