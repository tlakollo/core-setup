﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Microsoft.NET.HostModel.AppHost
{
    /// <summary>
    /// Embeds the App Name into the AppHost.exe
    /// If an apphost is a single-file bundle, updates the location of the bundle headers.
    /// </summary>
    public static class HostWriter
    {
        /// <summary>
        /// hash value embedded in default apphost executable in a place where the path to the app binary should be stored.
        /// </summary>
        private const string AppBinaryPathPlaceholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
        private readonly static byte[] AppBinaryPathPlaceholderSearchValue = Encoding.UTF8.GetBytes(AppBinaryPathPlaceholder);

        /// <summary>
        /// Create an AppHost with embedded configuration of app binary location
        /// </summary>
        /// <param name="appHostSourceFilePath">The path of Apphost template, which has the place holder</param>
        /// <param name="appHostDestinationFilePath">The destination path for desired location to place, including the file name</param>
        /// <param name="appBinaryFilePath">Full path to app binary or relative path to the result apphost file</param>
        /// <param name="windowsGraphicalUserInterface">Specify whether to set the subsystem to GUI. Only valid for PE apphosts.</param>
        /// <param name="assemblyToCopyResorcesFrom">Path to the intermediate assembly, used for copying resources to PE apphosts.</param>
        public static void CreateAppHost(
            string appHostSourceFilePath,
            string appHostDestinationFilePath,
            string appBinaryFilePath,
            bool windowsGraphicalUserInterface = false,
            string assemblyToCopyResorcesFrom = null)
        {
            var bytesToWrite = Encoding.UTF8.GetBytes(appBinaryFilePath);
            if (bytesToWrite.Length > 1024)
            {
                throw new AppNameTooLongException(appBinaryFilePath);
            }

            BinaryUtils.CopyFile(appHostSourceFilePath, appHostDestinationFilePath);

            try
            {
                // Re-write the destination apphost with the proper contents.
                bool appHostIsPEImage = false;
                using (var memoryMappedFile = MemoryMappedFile.CreateFromFile(appHostDestinationFilePath))
                {
                    using (MemoryMappedViewAccessor accessor = memoryMappedFile.CreateViewAccessor())
                    {
                        BinaryUtils.SearchAndReplace(accessor, AppBinaryPathPlaceholderSearchValue, bytesToWrite);

                        appHostIsPEImage = BinaryUtils.IsPEImage(accessor);

                        if (windowsGraphicalUserInterface)
                        {
                            if (!appHostIsPEImage)
                            {
                                throw new AppHostNotPEFileException();
                            }

                            BinaryUtils.SetWindowsGraphicalUserInterfaceBit(accessor);
                        }
                    }
                }

                if (assemblyToCopyResorcesFrom != null && appHostIsPEImage)
                {
                    if (ResourceUpdater.IsSupportedOS())
                    {
                        // Copy resources from managed dll to the apphost
                        new ResourceUpdater(appHostDestinationFilePath)
                            .AddResourcesFromPEImage(assemblyToCopyResorcesFrom)
                            .Update();
                    }
                    else
                    {
                        throw new AppHostCustomizationUnsupportedOSException();
                    }
                }

                // Memory-mapped write does not updating last write time
                File.SetLastWriteTimeUtc(appHostDestinationFilePath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Delete the destination file so we don't leave an unmodified apphost
                try
                {
                    File.Delete(appHostDestinationFilePath);
                }
                catch (Exception failedToDeleteEx)
                {
                    throw new AggregateException(ex, failedToDeleteEx);
                }

                throw;
            }
        }

        /// <summary>
        /// Set the current AppHost as a single-file bundle.
        /// </summary>
        /// <param name="appHostPath">The path of Apphost template, which has the place holder</param>
        /// <param name="bundleHeaderOffset">The offset to the location of bundle header</param>
        public static void SetAsBundle(
            string appHostPath,
            long bundleHeaderOffset)
        {
            byte[] bundleHeaderPlaceholder = {
                // 8 bytes represent the bundle header-offset 
                // Zero for non-bundle apphosts (default).
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                // 32 bytes represent the bundle signature: SHA-256 for ".net core bundle"
                0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
                0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
                0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
            };

            // Re-write the destination apphost with the proper contents.
            BinaryUtils.SearchAndReplace(appHostPath,
                                         bundleHeaderPlaceholder,
                                         BitConverter.GetBytes(bundleHeaderOffset), 
                                         pad0s:false);

            // Memory-mapped write does not updating last write time
            File.SetLastWriteTimeUtc(appHostPath, DateTime.UtcNow);
        }

        /// <summary>
        /// Check if the an AppHost is a single-file bundle
        /// </summary>
        /// <param name="appHostFilePath">The path of Apphost to check</param>
        /// <param name="bundleHeaderOffset">An out parameter containing the offset of the bundle header (if any)</param>
        /// <returns>True if the AppHost is a single-file bundle, false otherwise</returns>
        public static bool IsBundle(string appHostFilePath, out long bundleHeaderOffset)
        {
            byte[] bundleSignature = {
                // 32 bytes represent the bundle signature: SHA-256 for ".net core bundle"
                0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
                0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
                0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
            };

            using (var memoryMappedFile = MemoryMappedFile.CreateFromFile(appHostFilePath))
            {
                using (MemoryMappedViewAccessor accessor = memoryMappedFile.CreateViewAccessor())
                {
                    int position = BinaryUtils.SearchInFile(accessor, bundleSignature);
                    if(position == -1)
                    {
                        throw new PlaceHolderNotFoundInAppHostException(bundleSignature);
                    }

                    bundleHeaderOffset = accessor.ReadInt64(position - sizeof(Int64));

                    return (bundleHeaderOffset != 0);
                }
            }
        }
    }
}
