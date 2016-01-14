﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Microsoft.Exchange.WebServices.Data;
using Slalom.Huddle.OutlookApi.Models;

namespace Slalom.Huddle.OutlookApi.Services
{
    public class RoomLoader
    {
        private ExchangeService service;
        private string serviceAccount;

        public RoomLoader(ExchangeService service, string serviceAccount)
        {
            this.service = service;
            this.serviceAccount = serviceAccount;
        }

        public List<Room> LoadRooms(int preferredFloor)
        {
            List<Room> rooms = new List<Room>();

            // For each room in the Seattle conference address...
            foreach (var room in service.GetRooms("Slalom--SeattleConferenceRooms@slalom.com"))
            {
                // Skip any room that doesn't have the parsable data.
                if (!room.Name.Contains('['))
                    continue;

                RoomInfo roomInfo = RoomInfo.ParseRoomInfo(room.Name);

                // .. Create an attendee and a time window, which has to be at least 1 day.
                AttendeeInfo attendee = GetAttendeeFromRoom(room);
                rooms.Add(new Room { AttendeeInfo = attendee, RoomInfo = roomInfo });
            }

            // Sort by max people first, then floor.
            rooms = (from n in rooms
                     orderby Math.Abs(n.RoomInfo.Floor - preferredFloor) ascending,
                             n.RoomInfo.MaxPeople ascending
                     select n).ToList();
            return rooms;
        }

        public GetUserAvailabilityResults LoadRoomSchedule(List<Room> rooms, int duration)
        {
            TimeWindow timeWindow = new TimeWindow(DateTime.Now.ToUniversalTime().Date, DateTime.Now.ToUniversalTime().Date.AddDays(1));

            // We want to know if the room is free or busy.
            AvailabilityData availabilityData = AvailabilityData.FreeBusy;
            AvailabilityOptions availabilityOptions = new AvailabilityOptions();
            availabilityOptions.RequestedFreeBusyView = FreeBusyViewType.FreeBusy;
            availabilityOptions.MaximumSuggestionsPerDay = 0;

            // Get the availability of the room.
            var result = service.GetUserAvailability(from n in rooms
                                                     select n.AttendeeInfo,
                                                     timeWindow,
                                                     availabilityData,
                                                     availabilityOptions);

            // Use the schedule to determine if a room is available for the next
            DetermineRoomAvailability(rooms, result, duration);
            return result;
        }

        private static AttendeeInfo GetAttendeeFromRoom(EmailAddress room)
        {
            var attendee = new AttendeeInfo(room.Address);
            attendee.ExcludeConflicts = false;
            attendee.AttendeeType = MeetingAttendeeType.Organizer;

            return attendee;
        }

        private static void DetermineRoomAvailability(List<Room> rooms, GetUserAvailabilityResults result, int duration)
        {
            DateTime utcTime = DateTime.Now.ToUniversalTime();
            if (rooms.Count != result.AttendeesAvailability.Count)
            {
                throw new Exception($"The number of known rooms ({rooms.Count}) did not match the number of availabilities ({result.AttendeesAvailability.Count}).");
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                var availability = result.AttendeesAvailability[i];
                var room = rooms[i];

                room.Available = !(from n in result.AttendeesAvailability[i].CalendarEvents
                                   where (n.StartTime < utcTime && n.EndTime > utcTime) ||
                                   (n.StartTime > utcTime && n.StartTime < utcTime.AddMinutes(duration))
                                   select n).Any();
            }
        }

        public Appointment AcquireMeetingRoom(Room selectedRoom, int duration)
        {
            Appointment meeting = new Appointment(service);
            meeting.Subject = "Group Huddle";
            meeting.Body = $"I have scheduled '{selectedRoom.RoomInfo.Name}' for you on floor {selectedRoom.RoomInfo.Floor} for the next {duration} minutes";
            meeting.Start = DateTime.Now.ToLocalTime();
            meeting.End = meeting.Start.AddMinutes(duration);
            meeting.Location = $"{selectedRoom.RoomInfo.Name} on Floor {selectedRoom.RoomInfo.Floor}";
            meeting.RequiredAttendees.Add(selectedRoom.AttendeeInfo.SmtpAddress);
            meeting.RequiredAttendees.Add(serviceAccount);

            // Save the meeting to the Calendar folder and send the meeting request.
            meeting.Save(SendInvitationsMode.SendToAllAndSaveCopy);
            return meeting;
        }
    }
}