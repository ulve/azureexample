using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Net.Http;

namespace kvitton_demo2
{
    public static class Function1
    {
        [FunctionName("Starter")]
        public static void Run([BlobTrigger("kvitton/{name}", Connection = "BlobConnection")] Stream myBlob,
                               string name,
                               [OrchestrationClient] DurableOrchestrationClientBase starter)
        {
            var task = starter.StartNewAsync("Ocr", name);
            task.Wait();
        }

        [FunctionName("ApprovalProcessor")]
        public static async Task Approval([HttpTrigger(AuthorizationLevel.Function, methods: "post", Route = "approval/{instanceId}")] HttpRequestMessage req,
                                          string instanceId,
                                          [OrchestrationClient] DurableOrchestrationClient client,
                                          ILogger log)
        {
            log.LogInformation($"Approving id: {instanceId}");
            await client.RaiseEventAsync(instanceId, "IsKvitto", true);
        }

        [FunctionName("Ocr")]
        public static async Task<string> Ocr([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var blobName = context.GetInput<string>();

            var result = await context.CallActivityAsync<string>("GetTextFromImage", blobName);

            if (result.ToUpper().Contains("MOMS"))
            {
                log.LogInformation("Looks OK, sending to accounting!");
            }
            else
            {
                log.LogInformation($"This is not correct. Mailing actual person: {context.InstanceId}");
                var approved = await context.WaitForExternalEvent<bool>("IsKvitto");
                if(approved)
                    log.LogInformation($"The receipt is approved!");
                else
                    log.LogWarning($"This is not a receipt.");
            }

            return result;
        }

        [FunctionName("GetTextFromImage")]
        public static async Task<string> GetTextFromImage([ActivityTrigger] string blobName)
        {
            var computerVision = GetComputerVisionClient();

            var myBlob = await GetImageStream(blobName);
            myBlob.Position = 0;

            var textHeaders = await computerVision.RecognizeTextInStreamAsync(myBlob, TextRecognitionMode.Printed);
            var operationId = textHeaders.OperationLocation.Substring(textHeaders.OperationLocation.Length - 36);
            var result = await computerVision.GetTextOperationResultAsync(operationId);
            var i = 0;

            while ((result.Status == TextOperationStatusCodes.Running || result.Status == TextOperationStatusCodes.NotStarted) && i++ < 10)
            {
                await Task.Delay(1000);
                result = await computerVision.GetTextOperationResultAsync(operationId);
            }

            return string.Join(Environment.NewLine, result.RecognitionResult.Lines.Select(l => l.Text));
        }

        private static async Task<MemoryStream> GetImageStream(string blobName)
        {
            var storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("BlobConnection"));
            var myClient = storageAccount.CreateCloudBlobClient();
            var container = myClient.GetContainerReference("kvitton");
            var blockBlob = container.GetBlockBlobReference(blobName);

            var myBlob = new MemoryStream();

            await blockBlob.DownloadToStreamAsync(myBlob);

            return myBlob;
        }

        private static ComputerVisionClient GetComputerVisionClient()
        {
            var subscriptionKey = Environment.GetEnvironmentVariable("SubscriptionKey", EnvironmentVariableTarget.Process);
            var computerVision = new ComputerVisionClient(new ApiKeyServiceClientCredentials(subscriptionKey), new DelegatingHandler[] { })
            {
                Endpoint = "https://westcentralus.api.cognitive.microsoft.com/"
            };

            return computerVision;
        }
    }
}

