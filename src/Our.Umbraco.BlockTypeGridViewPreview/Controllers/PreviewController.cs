using Newtonsoft.Json;
using Our.Umbraco.BlockTypeGridViewPreview.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Web.Http;
using Umbraco.Core;
using Umbraco.Core.Composing;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.Blocks;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.Services;
using Umbraco.Web.Mvc;
using Umbraco.Web.PropertyEditors;
using Umbraco.Web.PropertyEditors.ValueConverters;
using Umbraco.Web.PublishedCache;
using Umbraco.Web.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.Blocks;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.PropertyEditors.ValueConverters;

namespace Our.Umbraco.BlockTypeGridViewPreview.Controllers
{
    [PluginController("BlockTypeGridViewPreview")]
    public class PreviewController : UmbracoAuthorizedApiController
    {
        private readonly IProfilingLogger _profilingLogger;
        private readonly IPublishedContentTypeFactory _publishedContentTypeFactory;
        private readonly IPublishedModelFactory _publishedModelFactory;
        private readonly IPublishedSnapshotAccessor _publishedSnapshotAccessor;
        private readonly BlockEditorConverter _blockConverter;
        private readonly BlockListEditorDataConverter _blockListEditorDataConverter;


        public PreviewController() { }

        public PreviewController(
            IProfilingLogger profilingLogger,
            IPublishedContentTypeFactory publishedContentTypeFactory,
            IPublishedModelFactory publishedModelFactory,
            IPublishedSnapshotAccessor publishedSnapshotAccessor, 
            BlockEditorConverter blockConverter)
        {
            _profilingLogger = profilingLogger;
            _publishedContentTypeFactory = publishedContentTypeFactory;
            _publishedModelFactory = publishedModelFactory;
            _publishedSnapshotAccessor = publishedSnapshotAccessor;
            _blockConverter = blockConverter;
            _blockListEditorDataConverter = new BlockListEditorDataConverter();
        }

        [HttpPost]
        public HttpResponseMessage GetBlockPreviewMarkup([FromBody] BlockPreview data)
        {
            var contentType = Services.ContentTypeService.Get(data.ContentTypeAlias);

            var editor = new BlockListPropertyValueConverter(
             _profilingLogger,
             new BlockEditorConverter(_publishedSnapshotAccessor, _publishedModelFactory));

            //This gets the property type for the property on the page, however it only works for the first level block.  
            //For blocks within blocks the editor doesn't seem to give us the property type of the 2nd level block anywhere and we need to figure something else out
            //var publishedContentType = new Lazy<IPublishedContentType>(() => _publishedContentTypeFactory.CreateContentType(contentType)).Value;
            //var propertyType = publishedContentType.PropertyTypes.FirstOrDefault(x => x.Alias == data.PropertyAlias);

            //var page = default(IPublishedContent);

            //// If the page is new, then the ID will be zero
            //// NOTE: the page isn't currently used in the block renderer, so I'm not sure if this is needed.
            //if (data.PageId > 0)
            //{
            //    // Get page container node
            //    page = Umbraco.Content(data.PageId);
            //    if (page == null)
            //    {
            //        // If unpublished, then fake PublishedContent
            //        page = new UnpublishedContent(data.PageId, Services, _publishedContentTypeFactory, Current.PropertyEditors);
            //    }
            //}

            //This wont work with multiple levels
            //var converted = editor.ConvertIntermediateToObject(page, propertyType, PropertyCacheLevel.None, data.Value, false) as BlockListModel;

            //This is needed since I can't figure out how to get the property type of the bock in the block renderer (if that's even possible).  
            //You can look at the page alias for the first level, but for blocks within bocks, the renderer needs a way to pass this in..
            //Copying the code her is a bad way to fix this, but once this is possible in the core (or someone figures out how to do it), we can remove this.
            var converted = ConvertIntermediateToObjectCustom(null, null, PropertyCacheLevel.None, data.Value, false) as BlockListModel;

            var model = converted[0];
            // Render view
            var partialName = string.Format(ConfigurationManager.AppSettings["BlockTypeGridViewPreviewPath"] ?? "~/Views/Partials/BlockList/Components/{0}.cshtml", model.Content.ContentType.Alias);
            var markup = Helpers.ViewHelper.RenderPartial(partialName, model, UmbracoContext.HttpContext, UmbracoContext);

            // Return response
            var response = new HttpResponseMessage
            {
                Content = new StringContent(markup ?? string.Empty)
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Text.Html);

            return response;
        }
     
        //A copy of the core BlockListValuePropertyConverter method, but if the propertyType is null, it doesn't validate each block agains the property type configuration and just returns the block model.
        //Could move this to the core, but it feels like there's a bigger problem with blocks here if they are supposed to be share across data types, there shuold be a single resolver here just to get the block models
        public object ConvertIntermediateToObjectCustom(IPublishedElement owner, IPublishedPropertyType propertyType, PropertyCacheLevel referenceCacheLevel, object inter, bool preview)
        {
            // NOTE: The intermediate object is just a json string, we don't actually convert from source -> intermediate since source is always just a json string


            BlockListConfiguration configuration = propertyType != null ? propertyType.DataType.ConfigurationAs<BlockListConfiguration>() : null;
            Dictionary<Guid, BlockListConfiguration.BlockConfiguration> blockConfigMap = configuration != null ? configuration.Blocks.ToDictionary(x => x.ContentElementTypeKey) : null;
            IList<Guid?> validSettingElementTypes = blockConfigMap != null ? blockConfigMap.Values.Select(x => x.SettingsElementTypeKey).Where(x => x.HasValue).Distinct().ToList() : null;

            var contentPublishedElements = new Dictionary<Guid, IPublishedElement>();
            var settingsPublishedElements = new Dictionary<Guid, IPublishedElement>();

            var layout = new List<BlockListItem>();

            var value = (string)inter;
            if (string.IsNullOrWhiteSpace(value)) return BlockListModel.Empty;

            var converted = _blockListEditorDataConverter.Deserialize(value);
            if (converted.BlockValue.ContentData.Count == 0) return BlockListModel.Empty;

            var blockListLayout = converted.Layout.ToObject<IEnumerable<BlockListLayoutItem>>();

            // convert the content data
            foreach (var data in converted.BlockValue.ContentData)
            {
                if (blockConfigMap != null && !blockConfigMap.ContainsKey(data.ContentTypeKey)) continue;

                var element = _blockConverter.ConvertToElement(data, referenceCacheLevel, preview);
                if (element == null) continue;
                contentPublishedElements[element.Key] = element;
            }
            // convert the settings data
            foreach (var data in converted.BlockValue.SettingsData)
            {
                if (validSettingElementTypes != null && !validSettingElementTypes.Contains(data.ContentTypeKey)) continue;

                var element = _blockConverter.ConvertToElement(data, referenceCacheLevel, preview);
                if (element == null) continue;
                settingsPublishedElements[element.Key] = element;
            }

            // if there's no elements just return since if there's no data it doesn't matter what is stored in layout
            if (contentPublishedElements.Count == 0) return BlockListModel.Empty;

            foreach (var layoutItem in blockListLayout)
            {
                // get the content reference
                var contentGuidUdi = (GuidUdi)layoutItem.ContentUdi;
                if (!contentPublishedElements.TryGetValue(contentGuidUdi.Guid, out var contentData))
                    continue;

                // get the setting reference
                IPublishedElement settingsData = null;
                var settingGuidUdi = layoutItem.SettingsUdi != null ? (GuidUdi)layoutItem.SettingsUdi : null;
                if (settingGuidUdi != null)
                    settingsPublishedElements.TryGetValue(settingGuidUdi.Guid, out settingsData);

                if (!contentData.ContentType.TryGetKey(out var contentTypeKey))
                    throw new InvalidOperationException("The content type was not of type " + typeof(IPublishedContentType2));

                BlockListConfiguration.BlockConfiguration blockConfig = null;
                if (blockConfigMap != null && !blockConfigMap.TryGetValue(contentTypeKey, out blockConfig))
                    continue;

                // this can happen if they have a settings type, save content, remove the settings type, and display the front-end page before saving the content again
                // we also ensure that the content type's match since maybe the settings type has been changed after this has been persisted.
                if (settingsData != null)
                {
                    if (!settingsData.ContentType.TryGetKey(out var settingsElementTypeKey))
                        throw new InvalidOperationException("The settings element type was not of type " + typeof(IPublishedContentType2));

                    if (blockConfig != null && (!blockConfig.SettingsElementTypeKey.HasValue || settingsElementTypeKey != blockConfig.SettingsElementTypeKey))
                        settingsData = null;
                }

                var layoutType = typeof(BlockListItem<,>).MakeGenericType(contentData.GetType(), settingsData?.GetType() ?? typeof(IPublishedElement));
                var layoutRef = (BlockListItem)Activator.CreateInstance(layoutType, contentGuidUdi, contentData, settingGuidUdi, settingsData);

                layout.Add(layoutRef);
            }

            var model = new BlockListModel(layout);
            return model;
        }
        


    }
}
